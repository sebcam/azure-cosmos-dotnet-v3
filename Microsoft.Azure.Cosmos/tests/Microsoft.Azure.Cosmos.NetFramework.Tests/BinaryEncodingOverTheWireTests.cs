//-----------------------------------------------------------------------
// <copyright file="CrossPartitionQueryTests.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.  All rights reserved.
// </copyright>
//-----------------------------------------------------------------------
namespace Microsoft.Azure.Cosmos.Services.Management.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.ObjectModel;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Net;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Client;
    using Microsoft.Azure.Cosmos.Internal;
    using Microsoft.VisualStudio.TestTools.UnitTesting;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Tests for CrossPartitionQueryTests.
    /// </summary>
    [Ignore]
    [TestClass]
    public class BinaryEncodingOverTheWireTests
    {
        private static readonly string[] NoDocuments = new string[] { };
        private static readonly DocumentClient GatewayClient = new DocumentClient(
            new Uri(ConfigurationManager.AppSettings["Endpoint"]),
            ConfigurationManager.AppSettings["MasterKey"],
            new ConnectionPolicy() { ConnectionMode = ConnectionMode.Gateway });
        private static readonly DocumentClient DirectHttpsClient = new DocumentClient(
            new Uri(ConfigurationManager.AppSettings["Endpoint"]),
            ConfigurationManager.AppSettings["MasterKey"],
            new ConnectionPolicy() { ConnectionMode = ConnectionMode.Direct, ConnectionProtocol = Protocol.Https });
        private static readonly DocumentClient RntbdClient = new DocumentClient(
            new Uri(ConfigurationManager.AppSettings["Endpoint"]),
            ConfigurationManager.AppSettings["MasterKey"],
            new ConnectionPolicy() { ConnectionMode = ConnectionMode.Direct, ConnectionProtocol = Protocol.Tcp});
        private static readonly DocumentClient[] DocumentClients = new DocumentClient[] { GatewayClient, DirectHttpsClient, RntbdClient };
        private static readonly DocumentClient Client = RntbdClient;
        private static CosmosDatabaseSettings database;

        [ClassInitialize]
        public static void Initialize(TestContext textContext)
        {
            BinaryEncodingOverTheWireTests.CleanUp();
        }

        [TestInitialize]
        public void TestInitialize()
        {
            BinaryEncodingOverTheWireTests.database = BinaryEncodingOverTheWireTests.CreateDatabase();
        }

        [TestCleanup]
        public void Cleanup()
        {
            BinaryEncodingOverTheWireTests.Client.DeleteDatabaseAsync(UriFactory.CreateDatabaseUri(BinaryEncodingOverTheWireTests.database.Id)).Wait();
        }

        private static CosmosDatabaseSettings CreateDatabase()
        {
            return BinaryEncodingOverTheWireTests.Client.CreateDatabaseAsync(
                new CosmosDatabaseSettings
                {
                   Id = Guid.NewGuid().ToString() + "db"
                }).Result;
        }

        private static CosmosContainerSettings CreateCollection()
        {
            return BinaryEncodingOverTheWireTests.Client.CreateDocumentCollectionAsync(
                UriFactory.CreateDatabaseUri(BinaryEncodingOverTheWireTests.database.Id),
                new CosmosContainerSettings
                {
                    Id = Guid.NewGuid().ToString() + "collection",
                    IndexingPolicy = new IndexingPolicy
                    {
                        IncludedPaths = new Collection<IncludedPath>
                        {
                            new IncludedPath
                            {
                                Path = "/*",
                                Indexes = new Collection<Index>
                                {
                                    RangeIndex.Range(DataType.Number, -1),
                                    RangeIndex.Range(DataType.String, -1),
                                }
                            }
                        }
                    },
                },
                new RequestOptions { OfferThroughput = 10000 }).Result;
        }

        private static async Task<Tuple<CosmosContainerSettings, List<Document>>> CreateCollectionAndIngestDocuments(IEnumerable<string> documents)
        {
            CosmosContainerSettings documentCollection = BinaryEncodingOverTheWireTests.CreateCollection();
            List<Document> insertedDocuments = new List<Document>();
            Random rand = new Random();
            foreach (string document in documents.OrderBy(x => rand.Next()).Take(100))
            {
                insertedDocuments.Add(await Client.CreateDocumentAsync(documentCollection.SelfLink, JsonConvert.DeserializeObject(document)));
            }

            return new Tuple<CosmosContainerSettings, List<Document>>(documentCollection, insertedDocuments);
        }

        private static void CleanUp()
        {
        }

        private delegate Task Query(DocumentClient documentClient, CosmosContainerSettings collection);

        /// <summary>
        /// Task that wraps boiler plate code for query tests (collection create -> ingest documents -> query documents -> delete collections).
        /// Note that this function will take the cross product connectionModes and collectionTypes.
        /// </summary>
        /// <param name="connectionModes">The connection modes to use.</param>
        /// <param name="collectionTypes">The type of collections to create.</param>
        /// <param name="documents">The documents to ingest</param>
        /// <param name="query">
        /// The callback for the queries.
        /// All the standard arguments will be passed in.
        /// Please make sure that this function is idempotent, since a collection will be reused for each connection mode.
        /// </param>
        /// <param name="partitionKey">The partition key for the partition collection.</param>
        /// <param name="testArgs">The optional args that you want passed in to the query.</param>
        /// <returns>A task to await on.</returns>
        private static async Task CreateIngestQueryDelete(
            IEnumerable<string> documents,
            Func<DocumentClient, CosmosContainerSettings, IEnumerable<Document>, dynamic, Task> query,
            dynamic testArgs = null)
        {
            Tuple<CosmosContainerSettings, List<Document>> collectionAndDocuments = await BinaryEncodingOverTheWireTests.CreateCollectionAndIngestDocuments(documents);

            bool succeeded = false;
            while (!succeeded)
            {
                try
                {
                    List<Task> queryTasks = new List<Task>();
                    foreach (DocumentClient documentClient in DocumentClients)
                    {
                        queryTasks.Add(query(documentClient, collectionAndDocuments.Item1, collectionAndDocuments.Item2, testArgs));
                    }

                    await Task.WhenAll(queryTasks);
                    succeeded = true;
                }
                catch (ServiceUnavailableException)
                {
                    // RNTBD throws ServiceUnavailableException every now and then
                }
            }

            await BinaryEncodingOverTheWireTests.Client.DeleteDocumentCollectionAsync(UriFactory.CreateDocumentCollectionUri(database.Id, collectionAndDocuments.Item1.Id));
        }

        private static async Task NoOp()
        {
            await Task.Delay(0);
        }

        [TestMethod]
        [TestCategory("Ignore") /* Used to filter out ignored tests in lab runs */]
        [Owner("brchon")]
        public void CheckThatAllTestsAreRunning()
        {
            // In general I don't want any of these tests being ignored or quarentined.
            // Please work with me if it needs to be.
            // I do not want these tests turned off for being "flaky", since they have been 
            // very stable and if they fail it's because something lower level is probably going wrong.

            Assert.AreEqual(0, typeof(BinaryEncodingOverTheWireTests)
                .GetMethods()
                .Where(method => method.GetCustomAttributes(typeof(TestMethodAttribute), true).Length != 0)
                .Where(method => method.GetCustomAttributes(typeof(TestCategoryAttribute), true).Length != 0)
                .Count(), $"One the {nameof(BinaryEncodingOverTheWireTests)} is not being run.");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task CombinedBingDocsTest()
        {
            await this.TestCurratedDocs("CombinedBingDocs.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task CombinedScriptsDataTest()
        {
            await this.TestCurratedDocs("CombinedScriptsData.json");
        }

        // For now we are skipping this test since the documents are too large to ingest and we get a rate size too large (HTTP 413).
#if TEST_COUNTRY
        [TestMethod]
        [Owner("brchon")]
        public async Task CountriesTest()
        {
            await this.TestCurratedDocs("countries.json");
        }
#endif

        [TestMethod]
        [Owner("brchon")]
        public async Task DevTestCollTest()
        {
            await this.TestCurratedDocs("devtestcoll.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task LastFMTest()
        {
            await this.TestCurratedDocs("lastfm.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task LogDataTest()
        {
            await this.TestCurratedDocs("LogData.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task MillionSong1KDocumentsTest()
        {
            await this.TestCurratedDocs("MillionSong1KDocuments.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task MsnCollectionTest()
        {
            await this.TestCurratedDocs("MsnCollection.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task NutritionDataTest()
        {
            await this.TestCurratedDocs("NutritionData.json");
        }
        
		[Ignore]
        [TestMethod]
        [Owner("brchon")]
        public async Task RunsCollectionTest()
        {
            await this.TestCurratedDocs("runsCollection.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task StatesCommitteesTest()
        {
            await this.TestCurratedDocs("states_committees.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task StatesLegislatorsTest()
        {
            await this.TestCurratedDocs("states_legislators.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task Store01Test()
        {
            await this.TestCurratedDocs("store01C.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task TicinoErrorBucketsTest()
        {
            await this.TestCurratedDocs("TicinoErrorBuckets.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task TwitterDataTest()
        {
            await this.TestCurratedDocs("twitter_data.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task Ups1Test()
        {
            await this.TestCurratedDocs("ups1.json");
        }

        [TestMethod]
        [Owner("brchon")]
        public async Task XpertEventsTest()
        {
            await this.TestCurratedDocs("XpertEvents.json");
        }

        private async Task TestCurratedDocs(string filename)
        {
            IEnumerable<object> documents = BinaryEncodingOverTheWireTests.GetDocumentsFromCurratedDoc(filename);
            await BinaryEncodingOverTheWireTests.CreateIngestQueryDelete(
                documents.Select(x => x.ToString()),
                this.TestCurratedDocs);
        }

        private async Task TestCurratedDocs(DocumentClient documentClient, CosmosContainerSettings collection, IEnumerable<Document> documents = null, dynamic testArgs = null)
        {
            await NoOp();
            HashSet<Document> inputDocuments = new HashSet<Document>(documents, DocumentEqualityComparer.Singleton);

            HashSet<Document> textDocuments = new HashSet<Document>(documentClient.CreateDocumentQuery<Document>(
                collection.AltLink,
                @"SELECT * FROM c",
                new FeedOptions { ContentSerializationFormat = ContentSerializationFormat.JsonText })
                .ToList(),
                DocumentEqualityComparer.Singleton);
            Assert.IsTrue(inputDocuments.SetEquals(textDocuments), "Text documents differ from input documents");

            try
            {
                HashSet<Document> binaryDocuments = new HashSet<Document>(documentClient.CreateDocumentQuery<Document>(
                    collection.AltLink,
                    @"SELECT * FROM c",
                    new FeedOptions { ContentSerializationFormat = ContentSerializationFormat.CosmosBinary })
                    .ToList(),
                    DocumentEqualityComparer.Singleton);
                Assert.IsTrue(inputDocuments.SetEquals(binaryDocuments), "Binary documents differ from input documents");

                Assert.Fail("Should throw an exception");
            }
            catch (Exception ex)
            {
                var innerException = ex.InnerException as DocumentClientException;
                Assert.AreEqual(HttpStatusCode.BadRequest, innerException.StatusCode, "Invalid status code");
            }
        }

        private static IEnumerable<object> GetDocumentsFromCurratedDoc(string filename)
        {
            string path = string.Format("TestJsons/{0}", filename);
            string json = File.ReadAllText(path);
            List<object> documents;
            try
            {
                documents = JsonConvert.DeserializeObject<List<object>>(json);
            }
            catch (JsonSerializationException)
            {
                documents = new List<object>();
                documents.Add(JsonConvert.DeserializeObject<object>(json));
            }

            return documents;
        }

        private sealed class DocumentEqualityComparer : IEqualityComparer<Document>
        {
            public static readonly DocumentEqualityComparer Singleton = new DocumentEqualityComparer();
            public bool Equals(Document x, Document y)
            {
                if(Object.ReferenceEquals(x, y))
                {
                    return true;
                }

                if (x == null || y == null)
                {
                    return false;
                }

                return JToken.DeepEquals(x.propertyBag, y.propertyBag);
            }

            public int GetHashCode(Document obj)
            {
                return JToken.EqualityComparer.GetHashCode(obj.propertyBag);
            }
        }
    }
}
