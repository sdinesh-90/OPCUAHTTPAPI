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
   public string Value => "true";
}

// Used in Settings.json
record Settings ([property: JsonPropertyName ("portNumber")] int PortNumber,
                 // Program end to start interval in second
                 [property: JsonPropertyName ("pgmEndToStartInterval")] double PgmEndToStartInterval = 1);

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
         mTimer.Start ();
         mTimer.Elapsed += OnTimerElapsed;
      }
   }
   static readonly object sLock = new ();

   public void ProgramCompleted (string pgmName, int quantity = -1) {
      if (mSettings == null) return;
      State = EMCState.Ended;
      Task.Delay ((int)(mSettings.PgmEndToStartInterval * 1000)).ContinueWith (a => ProgramStarted ("", 0));
   }

   public void ProgramStarted (string pgmName, int bendNo, int quantity = -1) {
      if (MachineStatus.Mode is EOperatingMode.SemiAuto or EOperatingMode.Auto)
         State = EMCState.Running;
   }

   public void ProgramStopped (string pgmName, int bendNo, int quantity = -1) {
      if (MachineStatus.Mode is EOperatingMode.SemiAuto or EOperatingMode.Auto)
         State = MachineStatus.IsInError ? EMCState.StoppedMalfunction : EMCState.StoppedOperator;
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
   #endregion

   #region Implementation -------------------------------------------
   string SettingsPath => Path.Combine (Environment.DataFolder, "opcua-settings.json");

   void SendState (EMCState state) {
      try {
         if (mSettings == null) return;
         var request = sClient.PutAsJsonAsync ($"https://localhost:{mSettings.PortNumber}/api/OpcUaNode/UpdateNodeValue", new Packet (0, DateTime.UtcNow, state));
         _ = request.Result;
      } catch (Exception e) {
         Console.WriteLine (e.Message);
      }
   }

   void OnTimerElapsed (object? o, ElapsedEventArgs e) {
      mTimer.Stop ();
      var mode = MachineStatus.Mode;
      if (mMode is EOperatingMode.SemiAuto or EOperatingMode.Auto) {
         if (mode is not EOperatingMode.SemiAuto and not EOperatingMode.Auto)
            State = EMCState.Aborted;
      }
      mMode = mode;
      mTimer.Start ();
   }
   EOperatingMode mMode = EOperatingMode.Program;
   #endregion

   #region Private Data ---------------------------------------------
   // Create an HttpClientHandler object and set to use default credentials
   static readonly HttpClient sClient = new ();
   Settings? mSettings;

   EMCState State {
      set => Task.Run (() => SendState (value));
   }

   readonly System.Timers.Timer mTimer = new (); // Timer to continuously log data
   #endregion
}