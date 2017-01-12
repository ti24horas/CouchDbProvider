namespace Microsoft.Extensions.Configuration.CouchDb
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Reactive.Disposables;
    using System.Reactive.Linq;
    using System.Reactive.Subjects;
    using System.Threading;
    using System.Threading.Tasks;

    using Configuration;

    using Microsoft.Extensions.Configuration.Json;
    using Microsoft.Extensions.FileProviders;

    using Newtonsoft.Json.Linq;

    using Primitives;

    internal static class CouchDbConfigWatcher
    {
        public static CouchDbChangeToken Get(string absoluteUrl, Action<string> onreload)
        {
            return new CouchDbChangeToken(absoluteUrl, onreload);
        }

        public class CouchDbChangeToken : IDisposable
        {
            private readonly string absoluteUrl;

            private readonly Action<string> onreload;

            private readonly CancellationTokenSource cts;

            private bool running;

            public CouchDbChangeToken(string absoluteUrl, Action<string> onreload)
            {
                this.absoluteUrl = absoluteUrl;
                this.onreload = onreload;

                this.cts = new CancellationTokenSource();
            }

            public void Run()
            {
                if (this.running)
                {
                    return;
                }

                this.running = true;

                var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(Timeout.Infinite) };

                Task.Factory.StartNew(async () => await ReaderContinuation(this.cts.Token, this.absoluteUrl, client, this.onreload), this.cts.Token);
            }

            public void Dispose()
            {
                this.cts.Cancel(false);
            }

            private static async Task ReaderContinuation(CancellationToken token, string absoluteUrl, HttpClient client, Action<string> reloadMethod)
            {
                var lastSeq = 0;

                CancellationTokenSource tks = null;

                while (!token.IsCancellationRequested)
                {
                    StreamReader reader;
                    try
                    {
                        var msg = new HttpRequestMessage(HttpMethod.Get, absoluteUrl);
                        var request = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, token);
                        reader = new StreamReader(await request.Content.ReadAsStreamAsync());
                    }
                    catch
                    {
                        await Task.Delay(1000, token);
                        continue;
                    }

                    using (reader)
                    {
                        while (true)
                        {
                            string s;
                            try
                            {
                                if (reader.EndOfStream)
                                {
                                    break;
                                }

                                s = await reader.ReadLineAsync();
                            }
                            catch
                            {
                                break;
                            }

                            if (string.IsNullOrWhiteSpace(s))
                            {
                                await Task.Delay(1000, token);
                                continue;
                            }

                            var obj = JObject.Parse(s);
                            JToken val;

                            if (obj.TryGetValue("seq", out val) && val.Value<int>() > lastSeq)
                            {
                                lastSeq = val.Value<int>();
                                reloadMethod(obj["id"].Value<string>());
                            }
                        }
                    }
                }
            }
        }
    }

    public class CouchDbFileProvider : IFileProvider
    {
        private readonly string host;

        private readonly int port;

        private readonly string database;

        private readonly CouchDbConfigWatcher.CouchDbChangeToken watcher;

        private readonly Subject<string> changes;

        private HttpClient client;

        public CouchDbFileProvider(string host, int port, string database)
        {
            this.host = host;
            this.port = port;
            this.database = database;
            var uri = new Uri($"http://{host}:{port}/{database}");

            this.client = new HttpClient { BaseAddress = uri };
            

            this.changes = new Subject<string>();

            this.watcher = CouchDbConfigWatcher.Get(
                  uri + "/_changes?feed=continuous",
                  (s) =>
                  {
                      this.changes.OnNext("/" + s);
                  });
            this.watcher.Run();

        }

        public IFileInfo GetFileInfo(string subpath)
        {
            return new CouchFile(this.client, subpath);
        }

        public IDirectoryContents GetDirectoryContents(string subpath)
        {
            var jsonString = this.client.SendAsync(new HttpRequestMessage(HttpMethod.Get, this.client.BaseAddress + "/_all_docs")).Result.Content.ReadAsStringAsync().Result;
            var json = JObject.Parse(jsonString);

            var rows = json["rows"];

            return new CouchFileContents(rows.Select(a=>new CouchFile(this.client, a["id"].Value<string>())));
        }

        private class CouchFileContents : IDirectoryContents
        {
            private readonly IEnumerable<CouchFile> files;

            public CouchFileContents(IEnumerable<CouchFile> files)
            {
                this.files = files;
                this.Exists = true;
            }

            public IEnumerator<IFileInfo> GetEnumerator() => this.files.GetEnumerator();

            IEnumerator IEnumerable.GetEnumerator()
            {
                return this.GetEnumerator();
            }

            public bool Exists { get; }
        }

        public IChangeToken Watch(string filter) => new ReactiveExtensionsChangeToken(this.changes.Where(a => a == filter));

        private class ReactiveExtensionsChangeToken : IChangeToken
        {
            private readonly IObservable<string> source;

            public ReactiveExtensionsChangeToken(IObservable<string> source)
            {
                this.source = source;
            }

            public IDisposable RegisterChangeCallback(Action<object> callback, object state)
            {
                return this.source.Take(1).Subscribe(s => callback(state));
            }

            public bool HasChanged { get; }

            public bool ActiveChangeCallbacks { get; }
        }

        private class CouchFile : IFileInfo
        {
            private readonly HttpClient client;

            private readonly string docId;

            private readonly Uri uri;

            public CouchFile(HttpClient client, string docId)
            {
                this.client = client;
                this.docId = docId;
                this.Exists = true;
                this.IsDirectory = false;
                this.Name = docId;
                this.PhysicalPath = docId.StartsWith("/") ? docId : "/" + docId;
            }

            public Stream CreateReadStream() => this.client.GetAsync(this.client.BaseAddress + this.PhysicalPath).Result.Content.ReadAsStreamAsync().Result;

            public bool Exists { get; }

            public long Length { get; }

            public string PhysicalPath { get; }

            public string Name { get; }

            public DateTimeOffset LastModified { get; }

            public bool IsDirectory { get; }
        }
    }

    public static class CouchDbProvider
    {
        public static IConfigurationBuilder AddCouchDb(this IConfigurationBuilder builder, string host, string database, int port = 5984, string document = null, bool reload = true)
        {
            var fileProvider = new CouchDbFileProvider(host, port, database);
            if (document != null)
            {
                builder.Add(new JsonConfigurationSource { FileProvider = fileProvider, Path = document, Optional = false, ReloadOnChange = reload });
                return builder;
            }

            foreach (var f in fileProvider.GetDirectoryContents(string.Empty).Where(a => a.Exists && !a.IsDirectory))
            {
                builder.Add(new JsonConfigurationSource { FileProvider = fileProvider, Path = f.PhysicalPath, Optional = false, ReloadOnChange = reload });
            }

            return builder;
        }
    }
}