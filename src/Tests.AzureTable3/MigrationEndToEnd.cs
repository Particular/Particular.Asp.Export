﻿namespace Tests.AzureTable3
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Cosmos;
    using Microsoft.Azure.Cosmos.Table;
    using NServiceBus;
    using NServiceBus.AcceptanceTesting;
    using NServiceBus.AcceptanceTesting.Customization;
    using NUnit.Framework;
    using Particular.Approvals;
    using Particular.AzureTable.Export;

    /*
     *  The test creates saga data in Azure Storage, then exports to a file in a working directory using the tool,
     *  then imports those files into Cosmos DB, then verifies that the data arrived correctly. The test is only
     *  run on the latest version of .NET because otherwise the Arrange step (setting up the data in Azure Storage)
     *  would be repeated for each test run. The first test run would succeed, and then attempt to delete the source
     *  table asynchronously. The second test run would likely not be able to create the table, because the delete
     *  would not have finished yet. We also can't orchestrate one run to set up the data, since constructs like
     *  [OneTimeSetUp] will run on every test run.
     */
    class MigrationEndToEnd : NServiceBusAcceptanceTest
    {
        [OneTimeSetUp]
        public async Task Setup()
        {
            var account = CloudStorageAccount.Parse(AzureStoragePersistenceConnectionString);
            var client = account.CreateCloudTableClient();

            table = client.GetTableReference(nameof(MigratingEndpoint.MigratingFromAzureTable3SagaData));

            await table.CreateIfNotExistsAsync();

            workingDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, Path.GetFileNameWithoutExtension(Path.GetTempFileName()));
            Directory.CreateDirectory(workingDir);
        }

        [OneTimeTearDown]
        public async Task Teardown()
        {
            await table.DeleteIfExistsAsync();
            Directory.Delete(workingDir, true);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task Can_migrate_from_ASP_to_CosmosDB(bool usePessimisticLocking)
        {
            // Arrange
            var testContext = await Scenario.Define<Context>(c => c.MyId = Guid.NewGuid())
                .WithEndpoint<MigratingEndpoint>(b => b.CustomConfig(ec =>
                {
                    var routing = ec.ConfigureTransport().Routing();
                    routing.RouteToEndpoint(typeof(CompleteSagaRequest), typeof(SomeOtherEndpoint));

                    var persistence = ec.UsePersistence<AzureTablePersistence>();
                    persistence.ConnectionString(AzureStoragePersistenceConnectionString);
                }).When((s, c) => s.SendLocal(new StartSaga
                {
                    MyId = c.MyId
                })))
                .Done(ctx => ctx.CompleteSagaRequestSent)
                .Run();

            // Act
            await Exporter.Run(new ConsoleLogger(true), AzureStoragePersistenceConnectionString, nameof(MigratingEndpoint.MigratingFromAzureTable3SagaData), workingDir, CancellationToken.None);

            var filePath = DetermineAndVerifyExport(testContext);
            await ImportIntoCosmosDB(filePath);

            // Assert
            testContext = await Scenario.Define<Context>(c => c.MyId = testContext.MyId)
                .WithEndpoint<MigratingEndpoint>(b => b.CustomConfig(ec =>
                {
                    var routing = ec.ConfigureTransport().Routing();
                    routing.RouteToEndpoint(typeof(CompleteSagaRequest), typeof(SomeOtherEndpoint));

                    var persistence = ec.UsePersistence<CosmosPersistence>();
                    persistence.CosmosClient(CosmosClient);
                    persistence.DatabaseName(DatabaseName);
                    persistence.DefaultContainer(ContainerName, PartitionPathKey);

                    if (usePessimisticLocking)
                    {
                        persistence.Sagas().UsePessimisticLocking();
                    }
                }))
                .WithEndpoint<SomeOtherEndpoint>()
                .Done(ctx => ctx.CompleteSagaResponseReceived)
                .Run();

            Approver.Verify(testContext.FromAsp3SagaData, s =>
            {
                return string.Join(Environment.NewLine, s.Split(Environment.NewLine).Where(l => !l.Contains("Id\":")));
            });
        }

        string DetermineAndVerifyExport(Context testContext)
        {
            var newId = CosmosSagaIdGenerator.Generate(typeof(MigratingEndpoint.MigratingFromAzureTable3SagaData).FullName, nameof(MigratingEndpoint.MigratingFromAzureTable3SagaData.MyId), testContext.MyId.ToString());

            var filePath = Path.Combine(workingDir, nameof(MigratingEndpoint.MigratingFromAzureTable3SagaData), $"{newId}.json");

            Assert.That(File.Exists(filePath), Is.True, "File exported");
            return filePath;
        }

        async Task ImportIntoCosmosDB(string filePath)
        {
            var container = CosmosClient.GetContainer(DatabaseName, ContainerName);

            var partitionKey = Path.GetFileNameWithoutExtension(filePath);

            using (var stream = File.OpenRead(filePath))
            {
                var response = await container.CreateItemStreamAsync(stream, new PartitionKey(partitionKey));

                Assert.That(response.IsSuccessStatusCode, Is.True, "Successfully imported");
            }
        }

        CloudTable table;
        string workingDir;

        public class Context : ScenarioContext
        {
            public bool CompleteSagaRequestSent { get; set; }
            public bool CompleteSagaResponseReceived { get; set; }

            public MigratingEndpoint.MigratingFromAzureTable3SagaData FromAsp3SagaData { get; set; }
            public Guid MyId { get; internal set; }
        }

        public class MigratingEndpoint : EndpointConfigurationBuilder
        {
            public MigratingEndpoint()
            {
                EndpointSetup<BaseEndpoint>();
            }

            public class MigratingSaga : Saga<MigratingFromAzureTable3SagaData>,
                IAmStartedByMessages<StartSaga>,
                IHandleMessages<CompleteSagaResponse>
            {
                public MigratingSaga(Context testContext)
                {
                    this.testContext = testContext;
                }

                public async Task Handle(StartSaga message, IMessageHandlerContext context)
                {
                    Data.MyId = message.MyId;

                    Data.ListOfStrings = new List<string> { "Hello World" };
                    Data.ListOfINts = new List<int> { 43, 42 };
                    Data.Nested = new Nested();
                    Data.IntValue = 1;
                    Data.LongValue = 1;
                    Data.DoubleValue = 1.24;
                    Data.BinaryValue = Encoding.UTF8.GetBytes("Hello World");
                    Data.DateTimeValue = new DateTime(2020, 09, 21, 5, 5, 5, 5, DateTimeKind.Utc);
                    Data.BooleanValue = true;
                    Data.FloatValue = 1.24f;
                    Data.DecimalValue = 1.24m;
                    Data.PretendsToBeAnArray = "[ Garbage ]";
                    Data.PretendsToBeAnObject = "{ \"Garbage\" }";
                    Data.Status = Status.Failed;

                    testContext.CompleteSagaRequestSent = true;
                    await context.Send(new CompleteSagaRequest());
                }

                public Task Handle(CompleteSagaResponse message, IMessageHandlerContext context)
                {
                    testContext.FromAsp3SagaData = Data;
                    testContext.CompleteSagaResponseReceived = true;

                    MarkAsComplete();
                    return Task.CompletedTask;
                }

                protected override void ConfigureHowToFindSaga(SagaPropertyMapper<MigratingFromAzureTable3SagaData> mapper)
                {
                    mapper.ConfigureMapping<StartSaga>(msg => msg.MyId).ToSaga(saga => saga.MyId);
                }

                readonly Context testContext;
            }

            public class MigratingFromAzureTable3SagaData : ContainSagaData
            {
                public Guid MyId { get; set; }
                public List<string> ListOfStrings { get; set; }
                public List<int> ListOfINts { get; set; }
                public Nested Nested { get; set; }

                public int IntValue { get; set; }
                public long LongValue { get; set; }
                public double DoubleValue { get; set; }
                public byte[] BinaryValue { get; set; }
                public DateTime DateTimeValue { get; set; }
                public bool BooleanValue { get; set; }
                public decimal DecimalValue { get; set; }
                public float FloatValue { get; set; }
                public string PretendsToBeAnArray { get; set; }
                public string PretendsToBeAnObject { get; set; }
                public Status Status { get; set; }
            }

            public class Nested
            {
                public string Foo { get; set; } = "Foo";
                public string Bar { get; set; } = "Bar";
            }

            public enum Status
            {
                Completed,
                Failed,
            }
        }

        public class SomeOtherEndpoint : EndpointConfigurationBuilder
        {
            public SomeOtherEndpoint()
            {
                EndpointSetup<BaseEndpoint>(c => c.UsePersistence<InMemoryPersistence>());
            }

            public class CompleteSagaRequestHandler : IHandleMessages<CompleteSagaRequest>
            {
                public Task Handle(CompleteSagaRequest message, IMessageHandlerContext context)
                {
                    return context.Reply(new CompleteSagaResponse());
                }
            }
        }

        public class StartSaga : ICommand
        {
            public Guid MyId { get; set; }
        }

        public class CompleteSagaRequest : IMessage
        {
        }

        public class CompleteSagaResponse : IMessage
        {
        }
    }
}