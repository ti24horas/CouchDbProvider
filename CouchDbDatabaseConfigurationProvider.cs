namespace Microsoft.Extensions.Configuration.CouchDb
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Net.Http;
    using System.Threading;
    using System.Threading.Tasks;

    using Configuration;

    using Newtonsoft.Json.Linq;

    using Primitives;

    public class CouchDbDatabaseConfigurationProvider : IConfigurationProvider, IConfigurationSource, IDisposable
    {
        private readonly string databasename;

        private readonly IDictionary<string, JToken> data = new Dictionary<string, JToken>();

        private readonly Uri uri;

        private readonly Stack<string> context = new Stack<string>();

        private readonly CouchDbConfigWatcher.CouchDbChangeToken watcher;

        private string currentPath;

        private ConfigurationReloadToken reload;

        public CouchDbDatabaseConfigurationProvider(string host, int port, string databasename, bool reload)
        {
            this.databasename = databasename;
            this.uri = new Uri($"http://{host}:{port}/{databasename}");
            if (reload)
            {
                this.watcher = CouchDbConfigWatcher.Get(
                  this.uri + "/_changes?feed=continuous",
                  () =>
                  {
                      this.Load();
                      this.reload.OnReload();
                  });
            }

            this.reload = new ConfigurationReloadToken();

            this.watcher.Run();
        }

        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string parentPath)
        {
            var configName = string.IsNullOrWhiteSpace(parentPath) ? string.Empty : parentPath + ConfigurationPath.KeyDelimiter;
            return from x in this.data
                   where x.Key.StartsWith(configName)
                   select Segment(x.Key, configName.Length);
        }

        public bool TryGet(string key, out string value)
        {
            JToken t;
            if (!this.data.TryGetValue(key, out t) || !(t is JValue))
            {
                value = null;
                return false;
            }

            JValue v = (JValue)t;

            var availableTokens = new[] { JTokenType.Boolean, JTokenType.Date, JTokenType.Float, JTokenType.Guid, JTokenType.Integer, JTokenType.Null, JTokenType.Raw, JTokenType.String, JTokenType.Uri };

            if (!availableTokens.Contains(v.Type))
            {
                value = null;
                return false;
            }

            value = v.Value.ToString();
            return true;
        }

        public void Set(string key, string value)
        {
            throw new NotImplementedException();
        }

        public IChangeToken GetReloadToken()
        {
            var token = new ConfigurationReloadToken();
            Interlocked.Exchange(ref this.reload, token);
            return token;
        }

        public void Load()
        {
            var client = new HttpClient();
            var request = client.GetAsync(this.uri + "/_all_docs?include_docs=true");
            var s = request.Result.Content.ReadAsStringAsync().Result;

            var json = JObject.Parse(s);

            this.EnterContext(this.databasename);
            foreach (var t in json["rows"].Select(d => new { _id = d["id"].Value<string>(), doc = d["doc"] }))
            {
                var doc = t.doc;
                switch (doc.Type)
                {
                    case JTokenType.Object:
                        var o = doc as JObject;

                        this.EnterContext(t._id);
                        this.VisitObject(o);
                        this.ExitContext();
                        break;
                }
            }

            this.ExitContext();
        }

        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return this;
        }

        public void Dispose()
        {
            this.watcher?.Dispose();
        }

        private static string Segment(string key, int prefixLength)
        {
            var num = key.IndexOf(ConfigurationPath.KeyDelimiter, prefixLength, StringComparison.OrdinalIgnoreCase);
            return num >= 0 ? key.Substring(prefixLength, num - prefixLength) : key.Substring(prefixLength);
        }

        private void VisitObject(JObject token)
        {
            foreach (var k in token.Properties())
            {
                if (k.Name[0] == '_')
                {
                    continue;
                }

                this.EnterContext(k.Name);
                if (k.Value.Type == JTokenType.Object)
                {
                    this.VisitObject(k.Value as JObject);
                }
                else
                {
                    this.data[this.currentPath] = k.Value.Value<string>();
                }

                this.ExitContext();
            }
        }

        private void EnterContext(string ctx)
        {
            this.context.Push(ctx);
            this.currentPath = ConfigurationPath.Combine(this.context.Reverse());
        }

        private void ExitContext()
        {
            this.context.Pop();
            this.currentPath = ConfigurationPath.Combine(this.context.Reverse());
        }

        private static class CouchDbConfigWatcher
        {
            public static CouchDbChangeToken Get(string absoluteUrl, Action onreload)
            {
                return new CouchDbChangeToken(absoluteUrl, onreload);
            }

            public class CouchDbChangeToken : IDisposable
            {
                private readonly string absoluteUrl;

                private readonly Action onreload;

                private readonly CancellationTokenSource cts;

                private bool running;

                public CouchDbChangeToken(string absoluteUrl, Action onreload)
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

                private static async Task ReaderContinuation(CancellationToken token, string absoluteUrl, HttpClient client, Action reloadMethod)
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
                                    var reload = new CancellationTokenSource(500);
                                    reload.Token.Register(reloadMethod);
                                    Interlocked.Exchange(ref tks, reload)?.Dispose();
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    public static class CouchDbProvider
    {
        public static IConfigurationBuilder AddCouchDb(this IConfigurationBuilder builder, string host, int port, string database, bool reload = true)
        {
            builder.Add(new CouchDbDatabaseConfigurationProvider(host, port, database, reload));
            return builder;
        }
    }
}