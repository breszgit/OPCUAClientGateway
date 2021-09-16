using Mono.Options;
using Opc.Ua;
using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using OPCUAClientGateway.Class;

namespace OPCUAClientGateway
{
    public class Program
    {
        public static dynamic AppConfig;
        public static string _LogStart;

        public static int Main(string[] args)
        {
            Console.WriteLine(
                (Utils.IsRunningOnMono() ? "Mono" : ".Net Core") + " OPC UA Client App");

            // command line options
            bool showHelp = false;
            int stopTimeout = Timeout.Infinite;
            bool autoAccept = false;

            Mono.Options.OptionSet options = new Mono.Options.OptionSet {
                { "h|help", "show this message and exit", h => showHelp = h != null },
                { "a|autoaccept", "auto accept certificates (for testing only)", a => autoAccept = a != null },
                { "t|timeout=", "the number of seconds until the client stops.", (int t) => stopTimeout = t }
            };

            IList<string> extraArgs = null;
            try
            {
                extraArgs = options.Parse(args);
                if (extraArgs.Count > 1)
                {
                    foreach (string extraArg in extraArgs)
                    {
                        Console.WriteLine("Error: Unknown option: {0}", extraArg);
                        showHelp = true;
                    }
                }
            }
            catch (OptionException e)
            {
                _LogStart += e.Message + Environment.NewLine;
                Console.WriteLine(e.Message);
                showHelp = true;
            }

            if (showHelp)
            {
                // show some app description message
                Console.WriteLine(Utils.IsRunningOnMono() ?
                    "Usage: mono MonoConsoleClient.exe [OPTIONS] [ENDPOINTURL]" :
                    "Usage: dotnet NetCoreConsoleClient.dll [OPTIONS] [ENDPOINTURL]");
                Console.WriteLine();

                // output the options
                Console.WriteLine("Options:");
                options.WriteOptionDescriptions(Console.Out);
                return (int)ExitCode.ErrorInvalidCommandLine;
            }

            //--Load Config--
            string JSConfig = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "appsettings.json"));
            AppConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(JSConfig);
            Console.WriteLine(AppConfig.OPC.EndpointURL);
            string endpointURL;
            if (extraArgs.Count == 0)
            {
                endpointURL = AppConfig.OPC.EndpointURL;
            }
            else
            {
                endpointURL = extraArgs[0];
            }

            SimpleClient client = new SimpleClient(endpointURL, autoAccept, stopTimeout, _LogStart);
            client.Run();

            // SimpleClient client = new SimpleClient("",false,10, "");
            // client.Run();
            

            return (int)SimpleClient.ExitCode;
        }
    }

    
}