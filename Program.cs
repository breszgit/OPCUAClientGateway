/* Copyright (c) 1996-2019 The OPC Foundation. All rights reserved.
   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else
   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/
   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2
   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using Mono.Options;
using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Splicer_OPCUA.Controllers;
using Splicer_OPCUA.Model;
using System.Linq;
using System.Text.Json;
using System.IO;

namespace NetCoreConsoleClient
{

    public enum ExitCode : int
    {
        Ok = 0,
        ErrorCreateApplication = 0x11,
        ErrorDiscoverEndpoints = 0x12,
        ErrorCreateSession = 0x13,
        ErrorBrowseNamespace = 0x14,
        ErrorCreateSubscription = 0x15,
        ErrorMonitoredItem = 0x16,
        ErrorAddSubscription = 0x17,
        ErrorRunning = 0x18,
        ErrorNoKeepAlive = 0x30,
        ErrorInvalidCommandLine = 0x100
    };

    public class Program
    {
        public static dynamic AppConfig;
        public static string _LogStart;

        public static int Main(string[] args)
        {
            Console.WriteLine(
                (Utils.IsRunningOnMono() ? "Mono" : ".Net Core") +
                " OPC UA Splicer App");

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

            string JSConfig = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "appsettings.json"));
            AppConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(JSConfig);
            
            string endpointURL;
            if (extraArgs.Count == 0)
            {
                // use OPC UA .Net Sample server 
                // endpointURL = "opc.tcp://172.31.204.116:4840/";
                endpointURL = AppConfig.OPC.URL;
            }
            else
            {
                endpointURL = extraArgs[0];
            }

            MySampleClient client = new MySampleClient(endpointURL, autoAccept, stopTimeout, _LogStart);
            client.Run();
            

            return (int)MySampleClient.ExitCode;
        }
    }

    public class MySampleClient
    {
        const int ReconnectPeriod = 10;
        Session session;
        SessionReconnectHandler reconnectHandler;
        string endpointURL;
        int clientRunTime = Timeout.Infinite;
        static bool autoAccept = false;
        static ExitCode exitCode;
        private static WebApiController ApiClient = new WebApiController();
        private static int CorNo = 2;
        private static string ApiURL = "https://localhost:5001/api/";
        private static List<Splicer> SPCs = new List<Splicer>();
        private static DateTime LastStamp;
        public static dynamic AppConfig;
        public static string LogPath = "";
        public static FileStream LogFile = null;
        private static int AppProcessID = 0;
        private static bool DisableLog = false;
        private static DateTime LastSync;
        private static double MinUpdateSec = 10;
        private static DateTime TimeToCreateLogFile;

        public MySampleClient(string _endpointURL, bool _autoAccept, int _stopTimeout, string _initLog)
        {
            endpointURL = _endpointURL;
            autoAccept = _autoAccept;
            clientRunTime = _stopTimeout <= 0 ? Timeout.Infinite : _stopTimeout * 1000;
            string JSConfig = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "appsettings.json"));
            AppConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(JSConfig);
            CorNo = AppConfig.OPC.CorID;
            ApiURL = AppConfig.OnboardTablet.Server;
            DisableLog = AppConfig.Setting.DisableLog;
            MinUpdateSec = AppConfig.Setting.MinUpdateSec;
            LastSync = DateTime.Now;

            if(DisableLog == false){
                string LogFolder = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)+"\\Log";
                if(!System.IO.Directory.Exists(LogFolder)){
                    System.IO.Directory.CreateDirectory(LogFolder);
                }
                LogPath = LogFolder+"\\Log_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".txt";
                LogFile = System.IO.File.Create(LogPath);
                LogFile.Dispose();
                TimeToCreateLogFile = DateTime.Now.AddHours(24);
            }
            
            WriteLog("Init_Log:"+_initLog);            
        }

        public void Run()
        {
            try
            {
                AppProcessID = System.Diagnostics.Process.GetCurrentProcess().Id;
                UpdateAppProcessID();

                ConsoleSampleClient().Wait();
            }
            catch (Exception ex)
            {
                Utils.Trace("ServiceResultException:" + ex.Message);
                Console.WriteLine("Exception: {0}", ex.Message);
                WriteLog("Error!!! Run:"+ex.Message);
                return;
            }

            ManualResetEvent quitEvent = new ManualResetEvent(false);
            try
            {
                Console.CancelKeyPress += (sender, eArgs) =>
                {
                    quitEvent.Set();
                    eArgs.Cancel = true;
                };
            }
            catch
            {
            }

            // wait for timeout or Ctrl-C
            quitEvent.WaitOne(clientRunTime);

            // return error conditions
            if (session.KeepAliveStopped)
            {
                exitCode = ExitCode.ErrorNoKeepAlive;
                return;
            }

            exitCode = ExitCode.Ok;
        }

        public static ExitCode ExitCode { get => exitCode; }

        private async Task ConsoleSampleClient()
        {
            Console.WriteLine("1 - Create an Application Configuration.");
            exitCode = ExitCode.ErrorCreateApplication;
            //Write Log
            WriteLog("Application start on "+DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));

            ApplicationInstance application = new ApplicationInstance
            {
                ApplicationName = "Splicer_UA_Corrugator_"+AppConfig.OPC.CorID.ToString(),
                // ApplicationName = "Splicer_UA_Corrugator",
                ApplicationType = ApplicationType.Client,
                ConfigSectionName = Utils.IsRunningOnMono() ? "Opc.Ua.MonoSampleClient" : "Opc.Ua.SampleClient"
            };
            
            // load the application configuration.
            ApplicationConfiguration config = null;

            try{
                WriteLog("Load config");
                // load the application configuration.
                config = await application.LoadApplicationConfiguration(false);                
            }
            catch(Exception e){
                WriteLog("Error!!! Load config. "+e.Message);
                return;
            }
            
            WriteLog("Check Certificate");
            bool haveAppCertificate = false;
            try{
                // check the application certificate.
                haveAppCertificate = await application.CheckApplicationInstanceCertificate(false, 0);
                if (!haveAppCertificate)
                {
                    WriteLog("Application instance certificate invalid!");
                    throw new Exception("Application instance certificate invalid!");
                }

                if (haveAppCertificate)
                {
                    config.ApplicationUri = Utils.GetApplicationUriFromCertificate(config.SecurityConfiguration.ApplicationCertificate.Certificate);
                    if (config.SecurityConfiguration.AutoAcceptUntrustedCertificates)
                    {
                        autoAccept = true;
                    }
                    config.CertificateValidator.CertificateValidation += new CertificateValidationEventHandler(CertificateValidator_CertificateValidation);
                }
                else
                {
                    Console.WriteLine("    WARN: missing application certificate, using unsecure connection.");
                    WriteLog("    WARN: missing application certificate, using unsecure connection.");
                }
            }
            catch(Exception ex){
                WriteLog("Error!!! Check Certificate"+ex.Message);
            }
            

            Console.WriteLine("2 - Discover endpoints of {0}.", endpointURL);
            WriteLog(string.Format("2 - Discover endpoints of {0}.", endpointURL));
            exitCode = ExitCode.ErrorDiscoverEndpoints;
            var selectedEndpoint = CoreClientUtils.SelectEndpoint(endpointURL, haveAppCertificate, 15000);
			selectedEndpoint.SecurityMode = MessageSecurityMode.None;
            selectedEndpoint.SecurityPolicyUri = SecurityPolicies.None;
            Console.WriteLine("    Selected endpoint uses: {0}",
                selectedEndpoint.SecurityPolicyUri.Substring(selectedEndpoint.SecurityPolicyUri.LastIndexOf('#') + 1));

            Console.WriteLine("3 - Create a session with OPC UA server.");
            exitCode = ExitCode.ErrorCreateSession;
            var endpointConfiguration = EndpointConfiguration.Create(config);
            var endpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
            session = await Session.Create(config, endpoint, false, "OPC UA Console Client", 60000, new UserIdentity("adm", "adm"), null);
            WriteLog("3 - Create a session with OPC UA server.");
            // register keep alive handler
            session.KeepAlive += Client_KeepAlive;

            Console.WriteLine("4 - Browse the OPC UA server namespace.");
            exitCode = ExitCode.ErrorBrowseNamespace;
            ReferenceDescriptionCollection references;
            Byte[] continuationPoint;

            references = session.FetchReferences(ObjectIds.ObjectsFolder);

            session.Browse(
                null,
                null,
                ObjectIds.ObjectsFolder,
                0u,
                BrowseDirection.Forward,
                ReferenceTypeIds.HierarchicalReferences,
                true,
                (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                out continuationPoint,
                out references);

            Console.WriteLine(" DisplayName, BrowseName, NodeClass");
            foreach (var rd in references)
            {
                Console.WriteLine(" {0}, {1}, {2}", rd.DisplayName, rd.BrowseName, rd.NodeClass);
                ReferenceDescriptionCollection nextRefs;
                byte[] nextCp;
                session.Browse(
                    null,
                    null,
                    ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris),
                    0u,
                    BrowseDirection.Forward,
                    ReferenceTypeIds.HierarchicalReferences,
                    true,
                    (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
                    out nextCp,
                    out nextRefs);

                foreach (var nextRd in nextRefs)
                {
                    Console.WriteLine("   + {0}, {1}, {2}", nextRd.DisplayName, nextRd.BrowseName, nextRd.NodeClass);
                }
            }

            Console.WriteLine("5 - Create a subscription with publishing interval of 1 second.");
            exitCode = ExitCode.ErrorCreateSubscription;
            var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };

            Console.WriteLine("6 - Add a list of items (server current time and status) to the subscription.");
            exitCode = ExitCode.ErrorMonitoredItem;
            var list = new List<MonitoredItem> {
                // new MonitoredItem(subscription.DefaultItem)
                // {
                //     DisplayName = "ServerStatusCurrentTime", StartNodeId = "i="+Variables.Server_ServerStatus_CurrentTime.ToString()
                // },
                //GL
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_DF_IEM_Liner_Rem_Current", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_DF_IEM_Liner_Rem_Current"
                },
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_DF_IEM_Liner_Rem_Previous", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_DF_IEM_Liner_Rem_Previous"
                },
                //BM
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_BF_IEM_Medium_Rem_Current", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_BF_IEM_Medium_Rem_Current"
                },
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_BF_IEM_Medium_Rem_Previous", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_BF_IEM_Medium_Rem_Previous"
                },
                //BL
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_BF_IEM_Liner_Rem_Current", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_BF_IEM_Liner_Rem_Current"
                },
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_BF_IEM_Liner_Rem_Previous", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_BF_IEM_Liner_Rem_Previous"
                },
                //CM
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_CF_IEM_Medium_Rem_Current", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_CF_IEM_Medium_Rem_Current"
                },
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_CF_IEM_Medium_Rem_Previous", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_CF_IEM_Medium_Rem_Previous"
                },
                //CL
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_CF_IEM_Liner_Rem_Current", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_CF_IEM_Liner_Rem_Current"
                },
                new MonitoredItem()
                {
                    DisplayName = "C"+CorNo.ToString()+"_CF_IEM_Liner_Rem_Previous", StartNodeId = "ns=4;s=C"+CorNo.ToString()+"_CF_IEM_Liner_Rem_Previous"
                },
            };
            list.ForEach(i => i.Notification += OnNotification);
            subscription.AddItems(list);

            Console.WriteLine("7 - Add the subscription to the session.");
            exitCode = ExitCode.ErrorAddSubscription;
            session.AddSubscription(subscription);
            subscription.Create();

            Console.WriteLine("8 - Running...Press Ctrl-C to exit...");
            exitCode = ExitCode.ErrorRunning;
        }

        private void Client_KeepAlive(Session sender, KeepAliveEventArgs e)
        {
            if (e.Status != null && ServiceResult.IsNotGood(e.Status))
            {
                Console.WriteLine("{0} {1}/{2}", e.Status, sender.OutstandingRequestCount, sender.DefunctRequestCount);
                WriteLog(string.Format("{0} {1}/{2}", e.Status, sender.OutstandingRequestCount, sender.DefunctRequestCount));
                
                if (reconnectHandler == null)
                {
                    Console.WriteLine("--- RECONNECTING ---");
                    reconnectHandler = new SessionReconnectHandler();
                    reconnectHandler.BeginReconnect(sender, ReconnectPeriod * 1000, Client_ReconnectComplete);
                }
            }
        }

        private void Client_ReconnectComplete(object sender, EventArgs e)
        {
            // ignore callbacks from discarded objects.
            if (!Object.ReferenceEquals(sender, reconnectHandler))
            {
                return;
            }

            session = reconnectHandler.Session;
            reconnectHandler.Dispose();
            reconnectHandler = null;

            Console.WriteLine("--- RECONNECTED ---");

            WriteLog("Reconnected");
        }

        private static void OnNotification(MonitoredItem item, MonitoredItemNotificationEventArgs e)
        {
            //Set Default Reginal
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("th-TH");
            System.Globalization.CultureInfo _cultureTH = new System.Globalization.CultureInfo("th-TH", true);
            foreach (var value in item.DequeueValues())
            {
                
                // Console.WriteLine(TimeZone.CurrentTimeZone);
                // Console.WriteLine("{0}: {1}, {2}, {3}", item.DisplayName, value.Value, value.SourceTimestamp, value.StatusCode);
                DateTime SoruceDateTime = value.SourceTimestamp.AddHours(7); //Update TimeZone
                if(LastStamp != SoruceDateTime){
                    Console.WriteLine("-----------------------------------");
                }
                Console.WriteLine("{0}: {1}, {2}, {3}", item.DisplayName, value.Value, SoruceDateTime, value.StatusCode);
            
                Int32 Remain = Convert.ToInt32(value.Value.ToString());
                string KeyDisplay = item.DisplayName;
                int EndIdx = (KeyDisplay.IndexOf(" ") > 0 ? KeyDisplay.IndexOf(" ") : (KeyDisplay.Length-2));
                KeyDisplay = KeyDisplay.Substring(2, KeyDisplay.Length-2);

                //Write Log
                WriteLog("Key:"+KeyDisplay+" Value:"+Remain.ToString());

                switch(KeyDisplay){
                    //--GL--
                    case "_DF_IEM_Liner_Rem_Current":
                        { AddSpliceData("GL",Remain,SoruceDateTime); break; }
                    case "_DF_IEM_Liner_Rem_Previous":
                        { AddSpliceData("GL",Remain,SoruceDateTime,true); break; }
                    //--BM--
                    case "_BF_IEM_Medium_Rem_Current":
                        { AddSpliceData("BM",Remain,SoruceDateTime); break; }
                    case "_BF_IEM_Medium_Rem_Previous":
                        { AddSpliceData("BM",Remain,SoruceDateTime,true); break; }
                    //--BL--
                    case "_BF_IEM_Liner_Rem_Current":
                        { AddSpliceData("BL",Remain,SoruceDateTime); break; }
                    case "_BF_IEM_Liner_Rem_Previous":
                        { AddSpliceData("BL",Remain,SoruceDateTime,true); break; }
                    //--CM--
                    case "_CF_IEM_Medium_Rem_Current":
                        { AddSpliceData("CM",Remain,SoruceDateTime); break; }
                    case "_CF_IEM_Medium_Rem_Previous":
                        { AddSpliceData("CM",Remain,SoruceDateTime,true); break; }
                    //--CL--
                    case "_CF_IEM_Liner_Rem_Current":
                        { AddSpliceData("CL",Remain,SoruceDateTime); break; }
                    case "_CF_IEM_Liner_Rem_Previous":
                        { AddSpliceData("CL",Remain,SoruceDateTime,true); break; }
                }
                LastStamp = SoruceDateTime;

                var DiffSync = (DateTime.Now - LastSync).TotalSeconds;
                if(DiffSync > MinUpdateSec){
                    SyncLastData();
                    LastSync = DateTime.Now;
                }
            }
        }

        private static void CertificateValidator_CertificateValidation(CertificateValidator validator, CertificateValidationEventArgs e)
        {
            if (e.Error.StatusCode == StatusCodes.BadCertificateUntrusted)
            {
                e.Accept = autoAccept;
                if (autoAccept)
                {
                    Console.WriteLine("Accepted Certificate: {0}", e.Certificate.Subject);
                }
                else
                {
                    Console.WriteLine("Rejected Certificate: {0}", e.Certificate.Subject);
                }
            }
        }

        
        #region AppEvent

        private static void UpdateAppProcessID(){
            string URL = string.Format("{0}{1}?CorID={2}&AppProcessID={3}",ApiURL,"Splicer/UpdateAppProcessID",CorNo,AppProcessID);
            
            var resp = ApiClient.CallWebApiwithObject(URL, null);

            WriteLog("Update AppProceID:"+resp.Data.ToString());
        }

        private static void OnSplice(string MRS, int Remain, int PreviousRemain, DateTime StampRemain, DateTime StampPrevious){
            Console.WriteLine("--Splice Trick ["+MRS+"]--");
            Splicer SPC = new Splicer(){
                Mrs = MRS,
                CorNo = CorNo,
                Remain = Remain,
                PreviousRemain = PreviousRemain,
                StampRemain = StampRemain,
                StampPreviousRemain = StampPrevious
            };

            string URL = ApiURL + "commu/SplicerUpdate";
            
            var resp = ApiClient.CallWebApiwithObject(URL, SPC);

            // Console.WriteLine(resp);
        }

        private static void AddSpliceData(string MRS, int Remain, DateTime Stamp, bool IsPrevious = false){
            var _SPC = SPCs.Where(x => x.Mrs == MRS).FirstOrDefault();
            if(_SPC == null){
                _SPC = new Splicer(){
                    Mrs = MRS,
                    CorNo = CorNo,
                    LastUpdate = DateTime.Now
                };                
                SPCs.Add(_SPC);
            }

            if(IsPrevious != true){
                _SPC.Remain = Remain;
                _SPC.StampRemain = Stamp;
                _SPC.LastUpdate = DateTime.Now;
            }
            else{
                _SPC.PreviousRemain = Remain;
                _SPC.StampPreviousRemain = Stamp;
                _SPC.LastUpdate = DateTime.Now;
                OnSplice(_SPC.Mrs, _SPC.Remain.Value, _SPC.PreviousRemain.Value, _SPC.StampRemain.Value, _SPC.StampPreviousRemain.Value);
            }
        }

        private static void SyncLastData(){
            var _SPC = SPCs.OrderByDescending(x => x.LastUpdate).FirstOrDefault();
            if(_SPC != null){
                OnSplice(_SPC.Mrs, _SPC.Remain.Value, _SPC.PreviousRemain.Value, _SPC.StampRemain.Value, _SPC.StampPreviousRemain.Value);
            }
        }

        public static void WriteLog(string Msg){
            if(DisableLog == true)
                return;

            //Create New Log File
            if(DateTime.Now > TimeToCreateLogFile)
                CreateLogFile();

            //Write Log
            using (StreamWriter writer = System.IO.File.AppendText(LogPath))
            {
                writer.WriteLine(Msg);
            }
        }

        public static void CreateLogFile(){
            string LogFolder = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)+"\\Log";
            if(!System.IO.Directory.Exists(LogFolder)){
                System.IO.Directory.CreateDirectory(LogFolder);
            }

            DirectoryInfo DirInfo = new DirectoryInfo(LogFolder);
            FileInfo[] filesInDir = DirInfo.GetFiles("*Log_" + DateTime.Now.ToString("yyyyMMdd") + "*.*");
            if(filesInDir.Length > 0)
                return;

            LogPath = LogFolder+"\\Log_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".txt";
            LogFile = System.IO.File.Create(LogPath);
            LogFile.Dispose();
            TimeToCreateLogFile = DateTime.Now.AddHours(24);

        }

        #endregion
    
    }
}