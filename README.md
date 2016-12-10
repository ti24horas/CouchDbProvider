# CouchDbProvider

This repository contains a minimal implementation for use CouchDb as configuration provider for ` Microsoft.Extensions.Configuration` framework.

## Usage

Currently I didn't published a nuget package. If you want to use it, add it to your project as a submodule and compile it together with your code.

See the code bellow to use it on your code:

``` csharp

namespace MyProject
{
    using Microsoft.Extensions.Configuration.CouchDb;

    public static class Program
    {

        public static void Main()
        {
            var configuration = new ConfigurationBuilder();

            // the code bellow connect to host and port, read all collections from database amd listen for changes
            configuration.AddCouchDb("your_ip_address", 5984, "configuration", true);
            configuration.AddCouchDb("your_ip_address", 5984, "redirections", true);

            var cfg = configuration.Build();

            // if you want to listen for changes, set the last parameter as true and connect to the token

            var section = cfg.GetSection("configuration");
            ChangeToken.OnChange(section.GetReloadToken, (IConfiguration state)=>
            {
                // here we should get the configuration already reloaded.

            }), section);

            // Be aware that the reload is called whenever the document is changed in couchdb. 
            // Currently there's no comparison for old/new values.

            /* suppose you have in database configuration a document in following format:
            {
                "_id": "some weird id",
                "_rev": "58-4fdde36260952fdc92dc667145af115a",
                
                "endpoints":{
                    "value1": "a",
                    "value2" : { "subkey": "1" }
                },
                "currentValue": "weird value"
            }*/

            var currentValue = cfg.GetSection("configuration")["currentValue"];

            Console.WriteLine(currentValue); // prints weird value

            var subsection = cfg.GetSection("configuration").GetSection("endpoints").GetSection("value2");

            Console.WriteLine(subsection["subkey"]); // prints 1
        }
    }

```