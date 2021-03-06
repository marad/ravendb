﻿using Raven.Abstractions.Exceptions;
using Raven.Abstractions.FileSystem;
using Raven.Client.FileSystem;
using Raven.Client.FileSystem.Listeners;
using Raven.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;
using Raven.Abstractions.Extensions;

namespace RavenFS.Tests.ClientApi
{
    public class FileSessionTests : RavenFsTestBase
    {

        private readonly IFilesStore filesStore;

        public FileSessionTests()
        {
            filesStore = this.NewStore();
        }

        [Fact]
        public void SessionLifecycle()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                Assert.NotNull(session.Advanced);
                Assert.True(session.Advanced.MaxNumberOfRequestsPerSession == 30);
                Assert.False(string.IsNullOrWhiteSpace(session.Advanced.StoreIdentifier));
                Assert.Equal(filesStore, session.Advanced.FilesStore);
                Assert.Equal(filesStore.Identifier, session.Advanced.StoreIdentifier.Split(';')[0]);
                Assert.Equal(store.DefaultFileSystem, session.Advanced.StoreIdentifier.Split(';')[1]);
            }

            store.Conventions.MaxNumberOfRequestsPerSession = 10;

            using (var session = filesStore.OpenAsyncSession())
            {
                Assert.True(session.Advanced.MaxNumberOfRequestsPerSession == 10);
            }
        }

        [Fact]
        public void EnsureMaxNumberOfRequestsPerSessionIsHonored()
        {
            var store = (FilesStore)filesStore;
            store.Conventions.MaxNumberOfRequestsPerSession = 0;

            using (var session = filesStore.OpenAsyncSession())
            {
                TaskAssert.Throws<InvalidOperationException>(() => session.LoadFileAsync("test1.file"));
                TaskAssert.Throws<InvalidOperationException>(() => session.DownloadAsync("test1.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterFileDeletion("test1.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterRename("test1.file", "test2.file"));
                Assert.Throws<InvalidOperationException>(() => session.RegisterUpload("test1.file", CreateUniformFileStream(128)));
            }
        }

        [Fact]
        public async void UploadWithDeferredAction()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 128; i++)
                        x.WriteByte(i);
                });

                await session.SaveChangesAsync();

                var file = await session.LoadFileAsync("test1.file");
                var resultingStream = await session.DownloadAsync(file);

                var ms = new MemoryStream();
                resultingStream.CopyTo(ms);
                ms.Seek(0, SeekOrigin.Begin);

                Assert.Equal(128, ms.Length);                

                for (byte i = 0; i < 128; i++)
                {
                    int value = ms.ReadByte();
                    Assert.True(value >= 0);
                    Assert.Equal(i, (byte)value);
                }
            }
        }

        [Fact]
        public void UploadActionWritesIncompleteStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                });

                TaskAssert.Throws<BadRequestException>(() => session.SaveChangesAsync());
            }
        }

        [Fact]
        public void UploadActionWritesIncompleteWithErrorStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                    
                    // We are throwing to break the upload. RavenFS client should detect this case and cancel the upload. 
                    throw new Exception();
                });

                TaskAssert.Throws<BadRequestException>(() => session.SaveChangesAsync());
            }
        }

        [Fact]
        public async void UploadAndDeleteFileOnDifferentSessions()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test2.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();
            }

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterFileDeletion("test1.file");

                var file = await session.LoadFileAsync("test1.file");
                Assert.NotNull(file);

                await session.SaveChangesAsync();

                file = await session.LoadFileAsync("test1.file");
                Assert.Null(file);
            }
        }

        [Fact]
        public async void RenameWithDirectoryChange()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("a/test1.file", CreateUniformFileStream(128));
                session.RegisterRename("a/test1.file", "a/a/test1.file");
                await session.SaveChangesAsync();

                var deletedFile = await session.LoadFileAsync("/a/test1.file");
                Assert.Null(deletedFile);

                var availableFile = await session.LoadFileAsync("/a/a/test1.file");
                Assert.NotNull(availableFile);
            }
        }

        [Fact]
        public async void RenameWithoutDirectoryChange()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                session.RegisterRename("/b/test1.file", "/b/test2.file");
                await session.SaveChangesAsync();

                var deletedFile = await session.LoadFileAsync("b/test1.file");
                Assert.Null(deletedFile);

                var availableFile = await session.LoadFileAsync("b/test2.file");
                Assert.NotNull(availableFile);
            }
        }

        [Fact]
        public async void EnsureSlashPrefixWorks()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                var fileWithoutPrefix = await session.LoadFileAsync("test1.file");
                var fileWithPrefix = await session.LoadFileAsync("/test1.file");
                Assert.NotNull(fileWithoutPrefix);
                Assert.NotNull(fileWithPrefix);

                fileWithoutPrefix = await session.LoadFileAsync("b/test1.file");
                fileWithPrefix = await session.LoadFileAsync("/b/test1.file");
                Assert.NotNull(fileWithoutPrefix);
                Assert.NotNull(fileWithPrefix);
            }
        }


        [Fact]
        public async void EnsureTwoLoadsWillReturnSameObject()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

               var firstCallFile = await session.LoadFileAsync("test1.file");
                var secondCallFile = await session.LoadFileAsync("test1.file");
                Assert.Equal(firstCallFile, secondCallFile);

                firstCallFile = await session.LoadFileAsync("/b/test1.file");
                secondCallFile = await session.LoadFileAsync("/b/test1.file");
                Assert.Equal(firstCallFile, secondCallFile);
            }
        }


        [Fact]
        public async void DownloadStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                var fileStream = CreateUniformFileStream(128);
                session.RegisterUpload("test1.file", fileStream);
                await session.SaveChangesAsync();

                fileStream.Seek(0, SeekOrigin.Begin);

                var file = await session.LoadFileAsync("test1.file");

                var resultingStream = await session.DownloadAsync(file);

                var originalText = new StreamReader(fileStream).ReadToEnd();
                var downloadedText = new StreamReader(resultingStream).ReadToEnd();
                Assert.Equal(originalText, downloadedText);

                //now downloading file with metadata

                Reference<RavenJObject> metadata = new Reference<RavenJObject>();
                resultingStream = await session.DownloadAsync("test1.file", metadata);

                Assert.NotNull(metadata.Value);
                Assert.Equal(128, metadata.Value.Value<long>("RavenFS-Size"));
            }
        }

        [Fact]
        public async void SaveIsIncompleteEnsureAllPendingOperationsAreCancelledStream()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                var fileStream = CreateUniformFileStream(128);
                session.RegisterUpload("test2.file", fileStream);
                session.RegisterUpload("test1.file", 128, x =>
                {
                    for (byte i = 0; i < 60; i++)
                        x.WriteByte(i);
                });
                session.RegisterRename("test2.file", "test3.file");

                TaskAssert.Throws<BadRequestException>(() => session.SaveChangesAsync());

                var shouldExist = await session.LoadFileAsync("test2.file");
                Assert.NotNull(shouldExist);
                var shouldNotExist = await session.LoadFileAsync("test3.file");
                Assert.Null(shouldNotExist);
            }
        }

        [Fact]
        public async void LoadMultipleFileHeaders()
        {
            var store = (FilesStore)filesStore;

            using (var session = filesStore.OpenAsyncSession())
            {
                session.RegisterUpload("/b/test1.file", CreateUniformFileStream(128));
                session.RegisterUpload("test1.file", CreateUniformFileStream(128));
                await session.SaveChangesAsync();

                var files = await session.LoadFileAsync(new String[] { "/b/test1.file", "test1.file" });

                Assert.NotNull(files);
            }
        }
  
    }
}
