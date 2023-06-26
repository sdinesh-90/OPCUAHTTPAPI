using Petrel;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Timers;

namespace OPCUARest;

public enum EMCState {
   Running, Ended, Stopped, StoppedMalfunction, StoppedOperator, Aborted,
}

[Brick]
class TrumpfOPCUA : IPgmState, IInitializable, IWhiteboard {
   public void BendChanged (string pgmName, int bendNo) {
   }

   static readonly HttpClient sClient = new ();

   void SendState (EMCState state) {
      try {
         if (mSettings == null) return;
         var request = sClient.PutAsJsonAsync ($"https://localhost:{mSettings.PortNumber}/api/OpcUaNode/UpdateNodeValue", new Packet (0, DateTime.Now, state));
         _ = request.Result;
      } catch (Exception e) {
         Console.WriteLine (e.Message);
      }
   }

   string SettingsPath => Path.Combine (Environment.DataFolder, "settings.json");

   public void Initialize () {
      lock (sLock) {
         string settingsPath = SettingsPath;
         if (File.Exists (settingsPath))
            mSettings = JsonSerializer.Deserialize<Settings> (File.ReadAllText (settingsPath));
         // Load default settings if no file is found
         mSettings ??= new (23591);
         mTimer.Start ();
         mTimer.Elapsed += OnTimerElapsed;
      }
   }

   void OnTimerElapsed (object? o, ElapsedEventArgs e) {
      mTimer.Stop ();
      var mode = MachineStatus.Mode;
      if (mMode is EOperatingMode.SemiAuto or EOperatingMode.Auto && mMode != mode) State = EMCState.Aborted;
      mMode = mode;
      mTimer.Start ();
   }
   EOperatingMode mMode = EOperatingMode.Program;

   public void ProgramCompleted (string pgmName, int quantity = -1) {
      State = EMCState.Ended;
   }

   public void ProgramStarted (string pgmName, int bendNo, int quantity = -1) {
      State = EMCState.Running;
   }

   public void ProgramStopped (string pgmName, int bendNo, int quantity = -1) {
      State = EMCState.Stopped;
   }

   public void Uninitialize () {
   }

   #region Whiteboard Implementation --------------------------------
   public IEnvironment Environment { set => sEnvironment = value; get => sEnvironment!; }
   static IEnvironment? sEnvironment;

   public IMachineStatus MachineStatus { set => sMachineStatus = value; get => sMachineStatus!; }
   static IMachineStatus? sMachineStatus;
   #endregion

   record Packet ([property: JsonPropertyName ("nodeType")] int NodeType,
                  [property: JsonPropertyName ("updateTime")] DateTime Time,
                  [property: JsonIgnore] EMCState State) {
      [JsonPropertyName ("nodeName")]
      public string NodeName => $"35.{State}";
      [JsonPropertyName ("nodeValue")]
      public string Value => "true";
   }

   // Used in Settings.json
   record Settings ([property: JsonPropertyName ("portNumber")] int PortNumber);

   Settings? mSettings;
   EMCState State {
      get => mState; set {
         if (value != mState) { mState = value; Task.Run (() => SendState (mState)); }
      }
   }
   EMCState mState;
   static readonly object sLock = new ();
   readonly System.Timers.Timer mTimer = new (); // Timer to continuously log data
}