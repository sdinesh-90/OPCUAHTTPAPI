using Petrel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;

namespace OPCUARest;

enum EMCState {
   Running, Ended, Stopped, StoppedMalfunction, StoppedOperator, Aborted,
}

record Packet ([property: JsonPropertyName ("nodeType")] int NodeType,
                  [property: JsonPropertyName ("updateTime")] DateTime Time,
                  [property: JsonIgnore] EMCState State) {
   [JsonPropertyName ("nodeName")]
   public string NodeName => $"35.{State}";
   [JsonPropertyName ("nodeValue")]
   public string Value { get; set; } = "true";
}

// Used in Settings.json
record Settings ([property: JsonPropertyName ("portNumber")] int PortNumber,
                 // Program end to start interval in second
                 [property: JsonPropertyName ("pgmEndToStartInterval")] double PgmEndToStartInterval = 1,
                 [property: JsonPropertyName ("useHTTPS")] bool UseHTTPS = true);

[Brick]
class TrumpfOPCUA : IPgmState, IInitializable, IWhiteboard {
   #region IPgmState Implementation ---------------------------------
   public void Initialize () {
      lock (sLock) {
         string settingsPath = SettingsPath;
         if (File.Exists (settingsPath))
            mSettings = JsonSerializer.Deserialize<Settings> (File.ReadAllText (settingsPath));
         // Load default settings if no file is found
         bool saveSettings = mSettings == null;
         mSettings ??= new (23591, 1);
         if (saveSettings) File.WriteAllText (settingsPath, JsonSerializer.Serialize (mSettings));
         System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12 |
                                                            System.Net.SecurityProtocolType.Tls11 |
                                                            System.Net.SecurityProtocolType.Tls;
         mTimer.Start ();
         mTimer.Elapsed += OnTimerElapsed;
      }
   }
   static readonly object sLock = new ();

   public void ProgramCompleted (string pgmName, int quantity = -1) {
      if (mSettings == null) return;
      DateTime time = DateTime.UtcNow;
      SetState (time, EMCState.Ended); ClearState (time, EMCState.Running, EMCState.Aborted, EMCState.Stopped, EMCState.StoppedMalfunction, EMCState.StoppedOperator);
      if (Job != null && (quantity < Job.QtyNeeded || quantity < 0))
         Task.Delay ((int)(mSettings.PgmEndToStartInterval * 1000)).ContinueWith (a => RaiseRunning ());
      else mPgmCompleted = true;
   }

   public void ProgramStarted (string pgmName, int bendNo, int quantity = -1) {
      if (mSettings == null) return;
      if (MachineStatus.Mode is EOperatingMode.SemiAuto or EOperatingMode.Auto) {
         if (bendNo == 0) {
            bool completed = mPgmCompleted;
            mPgmCompleted = false;
            if (!completed && Job != null && Job.QtyNeeded > 0) {
               DateTime time = DateTime.UtcNow;
               SetState (time, EMCState.Aborted);
               ClearState (time, EMCState.Running, EMCState.Stopped, EMCState.StoppedMalfunction, EMCState.StoppedOperator, EMCState.Ended);
               Task.Delay ((int)(mSettings.PgmEndToStartInterval * 1000)).ContinueWith (a => RaiseRunning ());
               mPgmCompleted = false;
               return;
            }
         }
         RaiseRunning ();
      }
   }
   
   void RaiseRunning () {
      DateTime time = DateTime.UtcNow;
      SetState (time, EMCState.Running);
      ClearState (time, EMCState.Aborted, EMCState.Stopped, EMCState.StoppedMalfunction, EMCState.StoppedOperator, EMCState.Ended);
   }
   bool mPgmCompleted;

   public void ProgramStopped (string pgmName, int bendNo, int quantity = -1) {
      if (mPgmCompleted) return;
      DateTime time = DateTime.UtcNow;
      SetState (time, EMCState.Stopped, MachineStatus.IsInError ? EMCState.StoppedMalfunction : EMCState.StoppedOperator);
      ClearState (time, EMCState.Running, EMCState.Aborted, EMCState.Ended);
   }

   public void BendChanged (string pgmName, int bendNo) {
   }

   public void Uninitialize () {
   }
   #endregion

   #region Whiteboard Implementation --------------------------------
   public IEnvironment Environment { set => sEnvironment = value; get => sEnvironment!; }
   static IEnvironment? sEnvironment;

   public IMachineStatus MachineStatus { set => sMachineStatus = value; get => sMachineStatus!; }
   static IMachineStatus? sMachineStatus;

   public IJob? Job { set => sJob = value; get => sJob; }
   static IJob? sJob;
   #endregion

   #region Implementation -------------------------------------------
   string SettingsPath => Path.Combine (Environment.DataFolder, "opcua-settings.json");

   void SendState (EMCState state, bool value, DateTime time) {
      try {
         if (mSettings == null) return;
         string url = mSettings.UseHTTPS ? "https" : "http"; 
         var request = sClient.PutAsJsonAsync ($"{url}://localhost:{mSettings.PortNumber}/api/OpcUaNode/UpdateNodeValue", new Packet (0, time, state) { Value = value ? "true" : "false" });
         _ = request.Result;
      } catch (Exception e) {
         Console.WriteLine (e.Message);
      }
   }

   void OnTimerElapsed (object? o, ElapsedEventArgs e) {
      mTimer.Stop ();
      var mode = MachineStatus.Mode;
      mMode = mode;
      mTimer.Start ();
   }
   EOperatingMode mMode = EOperatingMode.Program;
   #endregion

   #region Private Data ---------------------------------------------
   // Create an HttpClientHandler object and set to use default credentials
   static readonly HttpClient sClient = new ();
   Settings? mSettings;

   void SetState (DateTime time, params EMCState[] states) 
      => Task.Run (() => { foreach (var state in states) SendState (state, true, time); });

   void ClearState (DateTime time, params EMCState[] states)
      => Task.Run (() => { foreach (var state in states) SendState (state, false, time); });

   readonly System.Timers.Timer mTimer = new (); // Timer to continuously log data
   #endregion
}