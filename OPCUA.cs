using Petrel;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;

namespace OPCUARest;

enum EMCState {
   Running, Ended, Stopped, StoppedMalfunction, StoppedOperator, Aborted, ProgName, TargetQuantity, CurrentQuantity
}

record Packet ([property: JsonPropertyName ("nodeType")] int NodeType,
               [property: JsonPropertyName ("updateTime")] DateTime Time,
               [property: JsonPropertyName ("nodeName")] string Name,
               [property: JsonPropertyName ("nodeValue")] string Value);

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
      var states = new List<EMCState> ();
      states.Add (EMCState.Ended);
      mCurrentQuantity = quantity;
      states.Add (EMCState.CurrentQuantity);
      if (Job?.QtyNeeded == mCurrentQuantity) {
         mTargetQuantity = Job.QtyNeeded;
         states.Add (EMCState.TargetQuantity);
      }
      DateTime time = DateTime.UtcNow;
      SetState (time, states.ToArray ());
      ClearState (time, EMCState.Running, EMCState.Aborted, EMCState.Stopped, EMCState.StoppedMalfunction, EMCState.StoppedOperator, EMCState.ProgName);
      if (Job != null && (quantity < Job.QtyNeeded || quantity < 0))
         Task.Delay ((int)(mSettings.PgmEndToStartInterval * 1000)).ContinueWith (a => RaiseRunning ());
      else mPgmCompleted = true;
      mOverProduce = false;
   }

   public void ProgramStarted (string pgmName, int bendNo, int quantity = -1) {
      if (mSettings == null) return;
      if (MachineStatus.Mode is EOperatingMode.SemiAuto or EOperatingMode.Auto) {
         if (bendNo == 0) {
            bool completed = mPgmCompleted;
            mPgmCompleted = false;
            mOverProduce = Job != null && quantity >= Job.QtyNeeded;
            if (!completed && Job != null && Job.QtyNeeded > 0) {
               mProgName = pgmName;
               mTargetQuantity = Job.QtyNeeded;
               DateTime time = DateTime.UtcNow;
               SetState (time, EMCState.ProgName, EMCState.TargetQuantity, EMCState.Aborted);
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
   bool mPgmCompleted, mOverProduce;

   public void ProgramStopped (string pgmName, int bendNo, int quantity = -1) {
      if (Job != null && quantity >= Job.QtyNeeded && !mOverProduce) return;
      DateTime time = DateTime.UtcNow;
      SetState (time, EMCState.Stopped, MachineStatus.IsInError ? EMCState.StoppedMalfunction : EMCState.StoppedOperator);
      ClearState (time, EMCState.Running, EMCState.Aborted, EMCState.Ended, EMCState.ProgName);
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

   void SendState (string value, string name, int nodeType, DateTime time) {
      try {
         if (mSettings == null) return;
         string url = mSettings.UseHTTPS ? "https" : "http";
         var request = sClient.PutAsJsonAsync ($"{url}://localhost:{mSettings.PortNumber}/api/OpcUaNode/UpdateNodeValue", new Packet (nodeType, time, name, value));
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
   string mProgName = "";
   int mTargetQuantity = 0;
   int mCurrentQuantity = 0;

   void SetState (DateTime time, params EMCState[] states)
      => Task.Run (() => {
         foreach (var state in states) {
            (int type, string name, string value) = GetValues (state);
            SendState (value, name, type, time);
         }
      });

   void ClearState (DateTime time, params EMCState[] states)
      => Task.Run (() => {
         foreach (var state in states) {
            (int type, string name, string value) = GetValues (state, true);
            SendState (value, name, type, time);
         }
      });

   (int type, string name, string value) GetValues (EMCState state, bool isClear = false) =>
       state switch {
          EMCState.ProgName => (3, "32", isClear ? "" : mProgName),
          EMCState.TargetQuantity => (2, "12", isClear ? "0" : mTargetQuantity.ToString ()),
          EMCState.CurrentQuantity => (2, "13", isClear ? "0" : mCurrentQuantity.ToString ()),
          _ => (0, $"35.{state}", isClear ? "false" : "true")
       };
   readonly System.Timers.Timer mTimer = new (); // Timer to continuously log data
   #endregion
}