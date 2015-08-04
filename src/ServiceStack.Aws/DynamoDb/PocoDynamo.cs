﻿using System;
using System.Collections.Generic;
using System.Linq;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using ServiceStack.Logging;

namespace ServiceStack.Aws.DynamoDb
{
    public interface IPocoDynamo : IRequiresSchema, IDisposable
    {
        IAmazonDynamoDB DynamoDb { get; }
        ISequenceSource Sequences { get; }
        DynamoConverters Converters { get; }

        Table GetTableSchema(Type table);
        DynamoMetadataType GetTableMetadata(Type table);
        List<string> GetTableNames();
        bool CreateMissingTables(IEnumerable<DynamoMetadataType> tables, TimeSpan? timeout = null);
        bool DeleteAllTables(TimeSpan? timeout = null);
        bool DeleteTables(IEnumerable<string> tableNames, TimeSpan? timeout = null);
        T GetItemById<T>(object id);
        T GetItemByHashAndRange<T>(object hash, object range);
        T PutItem<T>(T value, bool returnOld = false);
        T DeleteItemById<T>(object hash, ReturnItem returnItem = ReturnItem.None);
        long IncrementById<T>(object id, string fieldName, long amount = 1);
        bool WaitForTablesToBeReady(IEnumerable<string> tableNames, TimeSpan? timeout = null);
        bool WaitForTablesToBeDeleted(IEnumerable<string> tableNames, TimeSpan? timeout = null);
        IPocoDynamo Clone();
    }

    public partial class PocoDynamo : IPocoDynamo
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(PocoDynamo));

        public IAmazonDynamoDB DynamoDb { get; private set; }

        public ISequenceSource Sequences { get; set; }

        public DynamoConverters Converters { get; set; }

        public bool ConsistentRead { get; set; }

        /// <summary>
        /// If the client needs to delete/re-create the DynamoDB table, this is the Read Capacity to use
        /// </summary>
        public long ReadCapacityUnits { get; set; }

        /// <summary>
        /// If the client needs to delete/re-create the DynamoDB table, this is the Write Capacity to use
        /// </summary> 
        public long WriteCapacityUnits { get; set; }

        public HashSet<string> RetryOnErrorCodes { get; set; }

        public TimeSpan PollTableStatus { get; set; }

        public TimeSpan MaxRetryOnExceptionTimeout { get; set; }


        public PocoDynamo(IAmazonDynamoDB dynamoDb)
        {
            this.DynamoDb = dynamoDb;
            this.Sequences = new DynamoDbSequenceSource(this);
            this.Converters = DynamoMetadata.Converters;
            PollTableStatus = TimeSpan.FromSeconds(2);
            MaxRetryOnExceptionTimeout = TimeSpan.FromSeconds(60);
            ReadCapacityUnits = 10;
            WriteCapacityUnits = 5;
            ConsistentRead = true;
            RetryOnErrorCodes = new HashSet<string> {
                "ThrottlingException",
                "ProvisionedThroughputExceededException",
                "LimitExceededException",
                "ResourceInUseException",
            };
        }

        public void InitSchema()
        {
            CreateMissingTables(DynamoMetadata.GetTables());
            Sequences.InitSchema();
        }

        public IPocoDynamo Clone()
        {
            return new PocoDynamo(DynamoDb)
            {
                ConsistentRead = ConsistentRead,
                ReadCapacityUnits = ReadCapacityUnits,
                WriteCapacityUnits = WriteCapacityUnits,
                RetryOnErrorCodes = new HashSet<string>(RetryOnErrorCodes),
                PollTableStatus = PollTableStatus,
                MaxRetryOnExceptionTimeout = MaxRetryOnExceptionTimeout,
            };
        }

        public DynamoMetadataType GetTableMetadata(Type table)
        {
            return DynamoMetadata.GetTable(table);
        }

        public List<string> GetTableNames()
        {
            return Exec(() => DynamoDb.ListTables().TableNames);
        }

        readonly Type[] throwNotFoundExceptions = {
            typeof(ResourceNotFoundException)
        };

        public Table GetTableSchema(Type type)
        {
            var table = DynamoMetadata.GetTable(type);
            return Exec(() =>
            {
                try
                {
                    Table awsTable;
                    Table.TryLoadTable(DynamoDb, table.Name, out awsTable);
                    return awsTable;
                }
                catch (ResourceNotFoundException)
                {
                    return null;
                }
            }, throwNotFoundExceptions);
        }

        public bool CreateMissingTables(IEnumerable<DynamoMetadataType> tables, TimeSpan? timeout = null)
        {
            var tablesList = tables.Safe().ToList();
            if (tablesList.Count == 0)
                return true;

            var existingTableNames = GetTableNames();

            foreach (var table in tablesList)
            {
                if (existingTableNames.Contains(table.Name))
                    continue;

                if (Log.IsDebugEnabled)
                    Log.Debug("Creating Table: " + table.Name);

                var request = ToCreateTableRequest(table);
                Exec(() => {
                    try
                    {
                        DynamoDb.CreateTable(request);
                    }
                    catch (AmazonDynamoDBException ex)
                    {
                        const string TableAlreadyExists = "ResourceInUseException";
                        if (ex.ErrorCode == TableAlreadyExists)
                            return;

                        throw;
                    }
                });
            }

            return WaitForTablesToBeReady(tablesList.Map(x => x.Name), timeout);
        }

        protected virtual CreateTableRequest ToCreateTableRequest(DynamoMetadataType table)
        {
            var props = table.Type.GetSerializableProperties();
            if (props.Length == 0)
                throw new NotSupportedException("{0} does not have any serializable properties".Fmt(table.Name));

            var keySchema = new List<KeySchemaElement> {
                new KeySchemaElement(table.HashKey.Name, KeyType.HASH),
            };
            var attrDefinitions = new List<AttributeDefinition> {
                new AttributeDefinition(table.HashKey.Name, table.HashKey.DbType),
            };
            if (table.RangeKey != null)
            {
                keySchema.Add(new KeySchemaElement(table.RangeKey.Name, KeyType.RANGE));
                attrDefinitions.Add(new AttributeDefinition(table.RangeKey.Name, table.RangeKey.DbType));
            }

            var to = new CreateTableRequest
            {
                TableName = table.Name,
                KeySchema = keySchema,
                AttributeDefinitions = attrDefinitions,
                ProvisionedThroughput = new ProvisionedThroughput
                {
                    ReadCapacityUnits = ReadCapacityUnits,
                    WriteCapacityUnits = WriteCapacityUnits,
                }
            };

            return to;
        }

        public bool DeleteAllTables(TimeSpan? timeout = null)
        {
            return DeleteTables(GetTableNames(), timeout);
        }

        public bool DeleteTables(IEnumerable<string> tableNames, TimeSpan? timeout = null)
        {
            foreach (var tableName in tableNames)
            {
                Exec(() => DynamoDb.DeleteTable(new DeleteTableRequest(tableName)));
            }

            return WaitForTablesToBeDeleted(tableNames);
        }

        public T GetItemById<T>(object id)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new GetItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(table.HashKey, id),
                ConsistentRead = ConsistentRead,
            };

            var response = Exec(() => DynamoDb.GetItem(request), throwNotFoundExceptions);

            if (!response.IsItemSet)
                return default(T);
            var attributeValues = response.Item;

            return DynamoMetadata.Converters.FromAttributeValues<T>(table, attributeValues);
        }

        public T GetItemByHashAndRange<T>(object hash, object range)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new GetItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(table, hash, range),
                ConsistentRead = ConsistentRead,
            };

            var response = Exec(() => DynamoDb.GetItem(request), throwNotFoundExceptions);

            if (!response.IsItemSet)
                return default(T);
            var attributeValues = response.Item;

            return DynamoMetadata.Converters.FromAttributeValues<T>(table, attributeValues);
        }

        public T PutItem<T>(T value, bool returnOld = false)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new PutItemRequest
            {
                TableName = table.Name,
                Item = Converters.ToAttributeValues(this, value, table),
                ReturnValues = returnOld ? ReturnValue.ALL_OLD : ReturnValue.NONE,
            };

            var response = Exec(() => DynamoDb.PutItem(request));

            if (response.Attributes.IsEmpty())
                return default(T);

            return Converters.FromAttributeValues<T>(table, response.Attributes);
        }

        public T DeleteItemById<T>(object hash, ReturnItem returnItem = ReturnItem.None)
        {
            var table = DynamoMetadata.GetTable<T>();
            var request = new DeleteItemRequest
            {
                TableName = table.Name,
                Key = Converters.ToAttributeKeyValue(table.HashKey, hash),
                ReturnValues = returnItem.ToReturnValue(),
            };

            var response = Exec(() => DynamoDb.DeleteItem(request));

            if (response.Attributes.IsEmpty())
                return default(T);

            return Converters.FromAttributeValues<T>(table, response.Attributes);
        }

        public long IncrementById<T>(object id, string fieldName, long amount = 1)
        {
            var type = DynamoMetadata.GetType<T>();
            var request = new UpdateItemRequest
            {
                TableName = type.Name,
                Key = Converters.ToAttributeKeyValue(type.HashKey, id),
                AttributeUpdates = new Dictionary<string, AttributeValueUpdate> {
                    {
                        fieldName,
                        new AttributeValueUpdate {
                            Action = AttributeAction.ADD,
                            Value = new AttributeValue { N = amount.ToString() }
                        }
                    }
                },
                ReturnValues = ReturnValue.ALL_NEW,
            };

            var response = DynamoDb.UpdateItem(request);

            return response.Attributes.Count > 0 
                ? Convert.ToInt64(response.Attributes[fieldName].N) 
                : 0;
        }

        public void Dispose()
        {
            if (DynamoDb == null)
                return;

            DynamoDb.Dispose();
            DynamoDb = null;
        }
    }
}