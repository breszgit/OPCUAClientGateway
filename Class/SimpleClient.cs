using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using OPCUAClientGateway.Model;
using Newtonsoft.Json;
using Microsoft.Data.SqlClient;

namespace OPCUAClientGateway.Class
{
    public class SimpleClient
    {
        const int ReconnectPeriod = 10;
        Session session;
        SessionReconnectHandler reconnectHandler;
        string endpointURL;
        int clientRunTime = Timeout.Infinite;
        static bool autoAccept = false;
        static ExitCode exitCode;
        private static WebApiClientClass ApiClient = new WebApiClientClass();
        // private static int CorNo = 2;
        // private static string ApiURL = "https://localhost:5001/api/";
        private static DateTime LastStamp;
        public static dynamic AppConfig;
        public static string LogPath = "";
        public static FileStream LogFile = null;
        private static int AppProcessID = 0;
        private static bool DisableLog = false;
        private static DateTime LastSync;
        private static double MinUpdateSec = 10;
        private static DateTime TimeToCreateLogFile;
        private static int LogSplitHour;
        private static int SubscriptInterval = 1000;
        private static List<TagItemModel> TagLists = new List<TagItemModel>();
        public static string Export_DB_ConString = "";
        public SimpleClient(string _endpointURL, bool _autoAccept, int _stopTimeout, string _initLog)
        {
            endpointURL = _endpointURL;
            autoAccept = _autoAccept;
            clientRunTime = _stopTimeout <= 0 ? Timeout.Infinite : _stopTimeout * 1000;
            string JSConfig = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "appsettings.json"));
            AppConfig = JsonConvert.DeserializeObject<dynamic>(JSConfig);
            Export_DB_ConString = AppConfig.ExportData.DB.ConString;
            DisableLog = AppConfig.Setting.DisableLog;
            MinUpdateSec = AppConfig.Setting.MinUpdateSec;
            LastSync = DateTime.Now;
            LogSplitHour = AppConfig.Setting.LogSplitHour; 
            SubscriptInterval = AppConfig.Setting.SubscriptInterval;
            foreach(var _tagItem in AppConfig.tagItems){
                TagItemModel _Tag = new TagItemModel(){
                    DisplayName = _tagItem.DisplayName,
                    StartNodeId = _tagItem.StartNodeId
                };
                TagLists.Add(_Tag);
            }            
            
            if(TagLists.Count == 0){
                WriteLog("TagList not found.");
                exitCode = ExitCode.ErrorTagNotfound;
                return;
            }                

            string LogFolderPath = AppConfig.Setting.LogFolderPath;
            if(DisableLog == false){
                string LogFolder = (!string.IsNullOrEmpty(LogFolderPath) ? LogFolderPath : Path.Combine(Environment.CurrentDirectory,"/Log"));
                
                if(!System.IO.Directory.Exists(LogFolder)){
                    System.IO.Directory.CreateDirectory(LogFolder);
                }
                LogPath = LogFolder+"\\Log_"+DateTime.Now.ToString("yyyyMMdd_HHmmss")+".txt";
                LogFile = System.IO.File.Create(LogPath);
                LogFile.Dispose();
                TimeToCreateLogFile = DateTime.Now.AddHours(LogSplitHour);
            }
            
            WriteLog("Init_Log:"+_initLog);    
            RemoveOldLogFile();
        }

        public void Run()
        {
            try
            {
                AppProcessID = System.Diagnostics.Process.GetCurrentProcess().Id;
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
            // ExportDataViaDB("Test", "", DateTime.Now, "BAD");

            Console.WriteLine("1 - Create an Application Configuration.");
            exitCode = ExitCode.ErrorCreateApplication;
            //Write Log
            WriteLog("Application start on "+DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));

            ApplicationInstance application = new ApplicationInstance
            {
                ApplicationName = "OPCUA_Conveyor",
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
            session = await Session.Create(config, endpoint, false, "OPC UA Console Client", 60000, null, null);
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

            // Console.WriteLine(" DisplayName, BrowseName, NodeClass");
            Console.WriteLine("Found "+references.Count+" items.");
            // foreach (var rd in references)
            // {
            //     Console.WriteLine(" {0}, {1}, {2}", rd.DisplayName, rd.BrowseName, rd.NodeClass);
            //     ReferenceDescriptionCollection nextRefs;
            //     byte[] nextCp;
            //     session.Browse(
            //         null,
            //         null,
            //         ExpandedNodeId.ToNodeId(rd.NodeId, session.NamespaceUris),
            //         0u,
            //         BrowseDirection.Forward,
            //         ReferenceTypeIds.HierarchicalReferences,
            //         true,
            //         (uint)NodeClass.Variable | (uint)NodeClass.Object | (uint)NodeClass.Method,
            //         out nextCp,
            //         out nextRefs);

            //     foreach (var nextRd in nextRefs)
            //     {
            //         Console.WriteLine("   + {0}, {1}, {2}", nextRd.DisplayName, nextRd.BrowseName, nextRd.NodeClass);
            //     }
            // }

            Console.WriteLine("5 - Create a subscription with publishing interval of 1 second.");
            exitCode = ExitCode.ErrorCreateSubscription;
            var subscription = new Subscription(session.DefaultSubscription) { PublishingInterval = 1000 };

            Console.WriteLine("6 - Add a list of items (server current time and status) to the subscription.");
            exitCode = ExitCode.ErrorMonitoredItem;

            var listMonitor = new List<MonitoredItem>();
            if(TagLists.Count > 0){
                foreach(var _Tag in TagLists){
                    MonitoredItem _TagMonitor = new MonitoredItem(){
                        DisplayName = _Tag.DisplayName,
                        StartNodeId = _Tag.StartNodeId
                    };
                    listMonitor.Add(_TagMonitor);
                }                
            }
            listMonitor.ForEach(i => i.Notification += OnNotification);
            subscription.AddItems(listMonitor);

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
            MonitoredItem _item = item;
            foreach (var value in item.DequeueValues())
            {
                DateTime SoruceDateTime = value.SourceTimestamp.AddHours(7); //Update TimeZone
                if(LastStamp != SoruceDateTime){
                    Console.WriteLine("-----------------------------------");
                    WriteLog(string.Format("-----{0}-----",DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss")));
                    
                }
                Console.WriteLine("{0}: {1}, {2}, {3}", item.DisplayName, value.Value, SoruceDateTime, value.StatusCode);
                
                // string KeyDisplay = _item.DisplayName;
                string KeyDisplay = GetRealKeyDisplay(_item.StartNodeId);

                //Write Log
                WriteLog("Key:"+KeyDisplay+" Value:"+value.Value.ToString());
                LastStamp = SoruceDateTime;

                //--SyncData to Server--
                if(AppConfig.ExportData.DB.Enable == true){
                    Task.Run(() => ExportDataViaDB(KeyDisplay, value.Value, SoruceDateTime, value.StatusCode.ToString()) );
                }

                if(AppConfig.ExportData.API.Enable == true){
                    Task.Run(() => ExportDataViaAPI(KeyDisplay, value.Value, SoruceDateTime, value.StatusCode.ToString()) );                    
                }

                // var DiffSync = (DateTime.Now - LastSync).TotalSeconds;
                // if(DiffSync > MinUpdateSec){
                //     SyncLastData();
                //     LastSync = DateTime.Now;
                // }
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

        #region  ExportData[DB]
        public static async void ExportDataViaDB(string Key, object Value, DateTime StampTime, string Status){
            await Task.Run(() => {
                SqlTransaction Trans = null;
                SqlConnection Conn = null;
                try{
                    string _ConString = Export_DB_ConString;
                    SqlCommand CMD = new SqlCommand();
                    Conn = new SqlConnection(_ConString); 
                    // Conn = new SqlConnection("Server=127.0.0.1;User Id=sa;Password=P@ssw0rd;Database=LocationTrackingReam"); 
                    Conn.Open();
                    Trans = Conn.BeginTransaction(System.Data.IsolationLevel.Serializable, "TransUpdateOPCData");
                    CMD.Connection = Conn;
                    CMD.Transaction = Trans;
                    
                    //--Check Row Exit--
                    CMD.CommandText = @"SELECT object_id FROM sys.tables WHERE name = 'OPCData';";
                    var TableExit = CMD.ExecuteScalar();
                    if(TableExit == null){
                        //--Create Table--
                        CMD.CommandText = @"CREATE TABLE OPCData (
            StampDate Datetime2 NOT NULL,
            DataKey nvarchar(100) NOT NULL,
            DataValue nvarchar(200),	
            CreateDate Datetime2,
            Status nvarchar(100),
            Notes nvarchar(250),
            CONSTRAINT PK_OPCData PRIMARY KEY (DataKey, StampDate)
        )";
                        CMD.ExecuteNonQuery();
                    }

                    //--Clear Old Data--
                    if(AppConfig.ExportData.DB.ClearEveryMin > 0){
                        Double _ClearMinute = Convert.ToDouble(AppConfig.ExportData.DB.ClearEveryMin*-1);
                        DateTime _ClearDate = DateTime.Now.AddMinutes(_ClearMinute);
                        if(_ClearDate.Year > 2500)
                            _ClearDate = _ClearDate.AddYears(-543);
                        string ClearDate = _ClearDate.ToString("yyyy-MM-dd HH:mm:ss");
                        Console.WriteLine("Cleare data at "+ ClearDate);
                        CMD.CommandText = @"DELETE FROM OPCData WHERE CreateDate <= @ClearDate";
                        CMD.Parameters.Add(new SqlParameter("@ClearDate",ClearDate));
                        CMD.ExecuteNonQuery();
                    }

                    //--Insert Data--
                    CMD.CommandText = @"INSERT INTO OPCData (StampDate, DataKey, DataValue, CreateDate, Status) 
                                        VALUES (@StampDate, @DataKey, @DataValue, GETDATE(), @Status)";
                    CMD.Parameters.Add(new SqlParameter("@StampDate",StampTime));
                    CMD.Parameters.Add(new SqlParameter("@DataKey",Key));
                    CMD.Parameters.Add(new SqlParameter("@DataValue",Value.ToString()));
                    CMD.Parameters.Add(new SqlParameter("@Status",Status));
                    CMD.ExecuteNonQuery();

                    Trans.Commit();
                    Conn.Close();
                }
                catch(Exception ex){
                    try
                    {
                        if(Trans != null)
                            Trans.Rollback();
                    }
                    catch (Exception ex2)
                    {
                        Console.WriteLine("Rollback Exception Type: {0}", ex2.GetType());
                        Console.WriteLine("  Message: {0}", ex2.Message);
                    }
                    if(Conn.State != System.Data.ConnectionState.Closed)
                        Conn.Close();

                    Console.WriteLine(ex.Message);
                }   
            });                     
        }
        #endregion

        #region  ExportData[API]
        public static async void ExportDataViaAPI(string Key, object Value, DateTime StampTime, string Status){
            await Task.Run(() => {
                string TargetURL = AppConfig.ExportData.API.TargetURL;
                var OutData = new {
                    DataKey = Key,
                    DataValue = Value,
                    StampTime,
                    Status
                };
                ApiClient.CallWebApiwithObject(TargetURL, OutData);
            });            
        }
        #endregion

        #region AppEvent        
        public static string GetRealKeyDisplay(NodeId _NID){
            string result = "";
            try{
                result = TagLists.Find(f => f.StartNodeId == _NID.ToString()).DisplayName;
            }
            catch(Exception ex){
                Console.WriteLine("Can't get KeyTag of {0}. {1}", _NID.ToString(), ex.Message);
            }            
            return result;
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
            TimeToCreateLogFile = DateTime.Now.AddHours(LogSplitHour);

            RemoveOldLogFile();
        }

        public static void RemoveOldLogFile(){
            string LogFolder = "";
            try{
                LogFolder = Path.GetDirectoryName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName)+"\\Log";
                if(!System.IO.Directory.Exists(LogFolder))
                    return;

                DirectoryInfo DirInfo = new DirectoryInfo(LogFolder);
                FileInfo[] filesInDir = DirInfo.GetFiles("*Log_*.*");
                if(filesInDir.Length > 0){
                    for(int i = filesInDir.Length-1; i >= 0; i--){
                        FileInfo FIO = filesInDir[i];
                        if(FIO.LastWriteTime < DateTime.Now.AddDays(-7)){
                            FIO.Delete();
                        }
                    }
                }
            }
            catch(Exception ex){
                WriteLog("RemoveLog Error!!! Path:"+ LogFolder +" "+ex.Message);
            }
            
        }

        #endregion
    
    }

}