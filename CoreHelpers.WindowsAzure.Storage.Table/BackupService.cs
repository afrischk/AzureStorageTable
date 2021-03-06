﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using CoreHelpers.WindowsAzure.Storage.Table.Internal;
using CoreHelpers.WindowsAzure.Storage.Table.Services;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace CoreHelpers.WindowsAzure.Storage.Table
{
    public class BackupService
    {
        private StorageContext tableStorageContext { get; set; }
        private CloudStorageAccount backupStorageAccount { get; set; }
        private IStorageLogger storageLogger { get; set; }

        private DataExportService dataExportService { get; set; }
        private DataImportService dataImportService { get; set; }

        public BackupService(StorageContext tableStorageContext, CloudStorageAccount backupStorageAccount, IStorageLogger storageLogger)
        {
            this.tableStorageContext = tableStorageContext;
            this.backupStorageAccount = backupStorageAccount;
            this.dataExportService = new DataExportService(tableStorageContext);
            this.dataImportService = new DataImportService(tableStorageContext);

            this.storageLogger = storageLogger;
        }

        public async Task Backup(string containerName, string targetPath, string[] excludedTables = null, string tableNamePrefix = null, bool compress = true)
        {
            // log 
            storageLogger.LogInformation($"Starting backup procedure...");

            // generate the excludeTables
            var excludedTablesList = new List<string>();
            if (excludedTables != null)
            {
                foreach (var tbl in excludedTables)
                    excludedTablesList.Add(tbl.ToLower());
            }

            // get all tables 
            var tables = await tableStorageContext.QueryTableList();
            storageLogger.LogInformation($"Processing {tables.Count} tables");

            // prepare the backup container
            var backupBlobClient = backupStorageAccount.CreateCloudBlobClient();
            var backupContainer = backupBlobClient.GetContainerReference(containerName.ToLower());
            storageLogger.LogInformation($"Creating target container {containerName} if needed");
            await backupContainer.CreateIfNotExistsAsync();

            // prepare the memory stats file
            var memoryStatsFile = $"{Path.GetTempFileName()}.csv";
            using (var statsFile = new StreamWriter(memoryStatsFile))
            {
                // use the statfile
                storageLogger.LogInformation($"Statsfile is under {memoryStatsFile}...");
                statsFile.WriteLine($"TableName,PageCounter,ItemCount,MemoryFootprint");

                // visit every table
                foreach (var tableName in tables)
                {
                    // filter the table prefix
                    if (!String.IsNullOrEmpty(tableNamePrefix) && !tableName.StartsWith(tableNamePrefix, StringComparison.CurrentCulture))
                    {
                        storageLogger.LogInformation($"Ignoring table {tableName}...");
                        continue;
                    }
                    else
                    {
                        storageLogger.LogInformation($"Processing table {tableName}...");
                    }

                    // excluded tables
                    if (excludedTablesList.Contains(tableName.ToLower())) 
                    {
                        storageLogger.LogInformation($"Ignoring table {tableName} (is part of excluded tables)...");
                        continue;
                    }

                    // check the excluded tables
                    // do the backup
                    var fileName = $"{tableName}.json";
                    if (!string.IsNullOrEmpty(targetPath)) { fileName = $"{targetPath}/{fileName}"; }
                    if (compress) { fileName += ".gz"; }

                    // open block blog reference
                    var blockBlob = backupContainer.GetBlockBlobReference(fileName);

                    // open the file stream 
                    if (compress)
                        storageLogger.LogInformation($"Writing backup to compressed file");
                    else
                        storageLogger.LogInformation($"Writing backup to non compressed file");

                    // do it

                    using (var backupFileStream = await blockBlob.OpenWriteAsync())
                    {
                        using (var contentWriter = new ZippedStreamWriter(backupFileStream, compress))
                        {
                            var pageCounter = 0;
                            await dataExportService.ExportToJson(tableName, contentWriter, (c) =>
                            {
                                pageCounter++;
                                storageLogger.LogInformation($"  Processing page #{pageCounter} with #{c} items...");
                                statsFile.WriteLine($"{tableName},{pageCounter},{c},{Process.GetCurrentProcess().WorkingSet64}");
                            });
                        }
                    }

                    // ensure we clean up the memory beause sometimes 
                    // we have to much referenced data
                    GC.Collect();

                    // flush the statfile 
                    await statsFile.FlushAsync();
                }
            }
        }

        public async Task Restore(string containerName, string srcPath, string tablePrefix = null) {

            // log 
            storageLogger.LogInformation($"Starting restore procedure...");

            // get all backup files 
            var blobClient = backupStorageAccount.CreateCloudBlobClient();
            var containerReference = blobClient.GetContainerReference(containerName.ToLower());

            // check if the container exists
            if (!await containerReference.ExistsAsync()) {
                storageLogger.LogInformation($"Missing container {containerName.ToLower()}");
                return;
            }

            // build the path including prefix 
            storageLogger.LogInformation($"Search Prefix is {srcPath}");

            // track the state
            var continuationToken = default(BlobContinuationToken);

            do
            {
                // get all blobs
                var blobResult = await containerReference.ListBlobsSegmentedAsync(srcPath, true, BlobListingDetails.All, 1000, continuationToken, null, null);

                // process every backup file as table 
                foreach(var blob in blobResult.Results) {

                    // build the name 
                    var blobName = blob.StorageUri.PrimaryUri.AbsolutePath;
                    blobName = blobName.Remove(0, containerName.Length + 2);

                    // get the tablename 
                    var tableName = Path.GetFileNameWithoutExtension(blobName);
                    var compressed = blobName.EndsWith(".gz", StringComparison.CurrentCultureIgnoreCase);
                    if (compressed)
                        tableName = Path.GetFileNameWithoutExtension(tableName);

                    // add the prefix
                    if (!String.IsNullOrEmpty(tablePrefix))
                        tableName = $"{tablePrefix}{tableName}";

                    // log
                    storageLogger.LogInformation($"Restoring {blobName} to table {tableName} (Compressed: {compressed})");

                    // build the reference
                    var blockBlobReference = containerReference.GetBlockBlobReference(blobName);

                    // open the read stream 
                    using (var readStream = await blockBlobReference.OpenReadAsync())
                    {
                        // unzip the stream 
                        using (var contentReader = new ZippedStreamReader(readStream, compressed))
                        {
                            // import the stream
                            var pageCounter = 0;
                            await dataImportService.ImportFromJsonStreamAsync(tableName, contentReader, (c) => {
                                pageCounter++;
                                storageLogger.LogInformation($"  Processing page #{pageCounter} with #{c} items...");
                            });
                        }
                    }
                }

                // proces the token 
                continuationToken = blobResult.ContinuationToken;

            } while (continuationToken != null);



        }
    }
}