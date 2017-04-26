﻿namespace NServiceBus.Transports.SQS
{
    using Amazon.S3;
    using Amazon.S3.Model;
    using Amazon.SQS;
    using Amazon.SQS.Model;
    using NServiceBus.AmazonSQS;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using NServiceBus.Logging;
    using Transport;
    using System.Threading.Tasks;

    class SqsQueueCreator : ICreateQueues
    {
        public SqsConnectionConfiguration ConnectionConfiguration { get; set; }

        public IAmazonS3 S3Client { get; set; }

        public IAmazonSQS SqsClient { get; set; }

        public Task CreateQueueIfNecessary(QueueBindings queueBindings, string identity)
        {
            var tasks = new List<Task>();

            foreach(var address in queueBindings.SendingAddresses)
            {
                tasks.Add(CreateQueueIfNecessary(address));
            }
            foreach(var address in queueBindings.ReceivingAddresses)
            {
                tasks.Add(CreateQueueIfNecessary(address));
            }
            return Task.WhenAll(tasks);
        }

        public async Task CreateQueueIfNecessary(string address)
        {
            try
            {
                var sqsRequest = new CreateQueueRequest
                {
                    QueueName = SqsQueueNameHelper.GetSqsQueueName(address, ConnectionConfiguration),
                };
                Logger.Info(String.Format("Creating SQS Queue with name \"{0}\" for address \"{1}\".", sqsRequest.QueueName, address));
                var createQueueResponse = await SqsClient.CreateQueueAsync(sqsRequest);

                // Set the queue attributes in a separate call. 
                // If you call CreateQueue with a queue name that already exists, and with a different
                // value for MessageRetentionPeriod, the service throws. This will happen if you 
                // change the MaxTTLDays configuration property. 
                var sqsAttributesRequest = new SetQueueAttributesRequest
                {
                    QueueUrl = createQueueResponse.QueueUrl
                };
                sqsAttributesRequest.Attributes.Add(QueueAttributeName.MessageRetentionPeriod,
                    ((int) (TimeSpan.FromDays(ConnectionConfiguration.MaxTTLDays).TotalSeconds)).ToString());

                await SqsClient.SetQueueAttributesAsync(sqsAttributesRequest);

                if (!string.IsNullOrEmpty(ConnectionConfiguration.S3BucketForLargeMessages))
                {
                    // determine if the configured bucket exists; create it if it doesn't
                    var listBucketsResponse = await S3Client.ListBucketsAsync(new ListBucketsRequest());
                    var bucketExists = listBucketsResponse.Buckets.Any(x => x.BucketName.ToLower() == ConnectionConfiguration.S3BucketForLargeMessages.ToLower());
                    if (!bucketExists)
                    {
                        await S3Client.RetryConflicts(async () =>
                        {
                            return await S3Client.PutBucketAsync(new PutBucketRequest
                            {
                                BucketName = ConnectionConfiguration.S3BucketForLargeMessages
                            });
                        },
                        onRetry: x =>
                        {
                            Logger.Warn($"Conflict when creating S3 bucket, retrying after {x}ms.");
                        });
                    }

                    await S3Client.RetryConflicts(async () =>
                    {
                        return await S3Client.PutLifecycleConfigurationAsync(new PutLifecycleConfigurationRequest
                        {
                            BucketName = ConnectionConfiguration.S3BucketForLargeMessages,
                            Configuration = new LifecycleConfiguration
                            {
                                Rules = new List<LifecycleRule>
                                {
                                    new LifecycleRule
                                    {
                                        Id = "NServiceBus.SQS.DeleteMessageBodies",
                                        Filter = new LifecycleFilter()
                                        {
                                            LifecycleFilterPredicate = new LifecyclePrefixPredicate
                                            {
                                                Prefix = ConnectionConfiguration.S3KeyPrefix
                                            }
                                        },
                                        Status = LifecycleRuleStatus.Enabled,
                                        Expiration = new LifecycleRuleExpiration
                                        {
                                            Days = ConnectionConfiguration.MaxTTLDays
                                        }
                                    }
                                }
                            }
                        });
                    },
                    onRetry: x =>
                    {
                        Logger.Warn($"Conflict when setting S3 lifecycle configuration, retrying after {x}ms.");
                    });
                }
            }
            catch (Exception e)
            {
                Logger.Error("Exception from CreateQueueIfNecessary.", e);
                throw;
            }
        }
        
        static ILog Logger = LogManager.GetLogger(typeof(SqsQueueCreator));
    }
}