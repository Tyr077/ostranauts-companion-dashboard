using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using LitJson;
using Ostranauts.Condowner;
using UnityEngine;

namespace CompanionServer
{
    [BepInPlugin("com.haiel.companionserver", "Companion Server", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // Config
        private ConfigEntry<int> _cfgPort;
        private ConfigEntry<string> _cfgBindAddress;
        private ConfigEntry<int> _cfgSnapshotInterval;
        private ConfigEntry<int> _cfgMaxEvents;

        // HTTP server
        private HttpListener _listener;
        private Thread _listenerThread;
        private volatile bool _running;

        // Thread-safe state cache
        private static readonly object _cacheLock = new object();
        private static string _crewJson = "[]";
        private static string _shipJson = "{}";
        private static string _statusJson = "{}";
        private static string _eventsJson = "[]";
        private static string _navJson = "{}";

        // Event log (ring buffer)
        private static readonly object _eventLock = new object();
        private static List<string> _eventLog = new List<string>();
        private static int _maxEvents = 100;

        // Command queue — background thread enqueues, main thread executes
        private static readonly object _cmdLock = new object();
        private static List<GameCommand> _commandQueue = new List<GameCommand>();
        private static List<string> _commandResults = new List<string>();

        // Snapshot timing
        private int _frameCounter;
        private int _snapshotInterval;
        private int _navFrameCounter;
        private const int NavSnapshotInterval = 1;  // every frame — 60Hz lightweight position data

        // Reflection cache (same pattern as CrewAIDiagnostics)
        private static FieldInfo _fiPriorities;
        private static FieldInfo _fiPriorityValue;
        private static FieldInfo _fiPriorityObjCond;

        private void Awake()
        {
            Log = Logger;

            _cfgPort = Config.Bind("Server", "Port", 8085, "HTTP server port");
            _cfgBindAddress = Config.Bind("Server", "BindAddress", "0.0.0.0",
                "Bind address. Use 0.0.0.0 for LAN access, 127.0.0.1 for local only");
            _cfgSnapshotInterval = Config.Bind("Server", "SnapshotInterval", 30,
                "Frames between state snapshots (~0.5s at 60fps)");
            _cfgMaxEvents = Config.Bind("Server", "MaxEventLog", 100,
                "Maximum AI events to keep in memory");

            _snapshotInterval = _cfgSnapshotInterval.Value;
            _maxEvents = _cfgMaxEvents.Value;

            // Cache reflection for Priority type (same as CrewAIDiagnostics)
            _fiPriorities = AccessTools.Field(typeof(CondOwner), "aPriorities");
            if (_fiPriorities != null)
            {
                Type priorityType = _fiPriorities.FieldType.GetGenericArguments()[0];
                _fiPriorityValue = AccessTools.Field(priorityType, "fValue");
                _fiPriorityObjCond = AccessTools.Field(priorityType, "objCond");
            }

            StartServer();
            Log.LogInfo("Companion Server v1.0.0 loaded — http://"
                + _cfgBindAddress.Value + ":" + _cfgPort.Value + "/api/status");
        }

        private void StartServer()
        {
            int port = _cfgPort.Value;
            string bind = _cfgBindAddress.Value;

            _listener = new HttpListener();

            // Try binding in order of preference:
            // 1. http://+:port/ (LAN accessible, requires URL ACL on Windows)
            // 2. http://*:port/ (same, alternate syntax)
            // 3. http://localhost:port/ (local only, no admin needed)
            string[] prefixAttempts;
            if (bind == "0.0.0.0" || bind == "+")
            {
                prefixAttempts = new string[] {
                    "http://+:" + port + "/",
                    "http://*:" + port + "/",
                    "http://localhost:" + port + "/"
                };
            }
            else
            {
                prefixAttempts = new string[] {
                    "http://" + bind + ":" + port + "/"
                };
            }

            foreach (string prefix in prefixAttempts)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add(prefix);
                    _listener.Start();
                    _running = true;

                    _listenerThread = new Thread(ListenLoop)
                    {
                        IsBackground = true,
                        Name = "CompanionServer"
                    };
                    _listenerThread.Start();

                    Log.LogInfo("HTTP server listening on " + prefix);
                    return;
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Could not bind to " + prefix + ": " + ex.Message);
                    try { _listener.Stop(); } catch { }
                    _listener = new HttpListener();
                }
            }

            Log.LogError("Failed to start HTTP server on any prefix!");
            Log.LogError("For LAN access, run as admin: netsh http add urlacl url=http://+:" + port + "/ user=Everyone");
        }

        private void ListenLoop()
        {
            while (_running && _listener != null && _listener.IsListening)
            {
                try
                {
                    var ctx = _listener.GetContext();
                    ThreadPool.QueueUserWorkItem(_ => HandleRequest(ctx));
                }
                catch (HttpListenerException)
                {
                    // Expected when stopping
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log.LogError("Listener error: " + ex.Message);
                }
            }
        }

        private void HandleRequest(HttpListenerContext ctx)
        {
            try
            {
                string path = ctx.Request.Url.AbsolutePath.ToLower().TrimEnd('/');
                string json;

                // CORS headers for browser + API access
                ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
                ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                ctx.Response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");

                if (ctx.Request.HttpMethod == "OPTIONS")
                {
                    ctx.Response.StatusCode = 204;
                    ctx.Response.Close();
                    return;
                }

                // POST endpoints — command system
                if (ctx.Request.HttpMethod == "POST")
                {
                    json = HandlePostCommand(ctx, path);
                }
                else switch (path)
                {
                    case "/api/status":
                        lock (_cacheLock) { json = _statusJson; }
                        break;
                    case "/api/crew":
                        lock (_cacheLock) { json = _crewJson; }
                        break;
                    case "/api/ship":
                        lock (_cacheLock) { json = _shipJson; }
                        break;
                    case "/api/events":
                        lock (_cacheLock) { json = _eventsJson; }
                        break;
                    case "/api/nav":
                        lock (_cacheLock) { json = _navJson; }
                        break;
                    case "/api/commands/results":
                        lock (_cmdLock)
                        {
                            var rsb = new StringBuilder("[");
                            for (int i = 0; i < _commandResults.Count; i++)
                            {
                                if (i > 0) rsb.Append(",");
                                rsb.Append(_commandResults[i]);
                            }
                            rsb.Append("]");
                            json = rsb.ToString();
                            _commandResults.Clear();
                        }
                        break;
                    default:
                        json = "{\"error\":\"Unknown endpoint\"}";
                        ctx.Response.StatusCode = 404;
                        break;
                }

                byte[] buffer = Encoding.UTF8.GetBytes(json);
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = buffer.Length;
                ctx.Response.OutputStream.Write(buffer, 0, buffer.Length);
                ctx.Response.Close();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Request handler error: " + ex.Message);
                try { ctx.Response.Close(); } catch { }
            }
        }

        private void Update()
        {
            // Always process commands (every frame for responsiveness)
            try
            {
                ExecuteCommands();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Command execution error: " + ex.Message);
            }

            if (CrewSim.coPlayer == null) return;

            // Nav snapshot — fast interval (every 5 frames ≈ 12Hz)
            // Lightweight: just positions/velocities, no reflection or string lookups
            _navFrameCounter++;
            if (_navFrameCounter >= NavSnapshotInterval)
            {
                _navFrameCounter = 0;
                try
                {
                    var navSb = new StringBuilder(8192);
                    string navJson = SnapshotNav(navSb);
                    lock (_cacheLock) { _navJson = navJson; }
                }
                catch (Exception ex)
                {
                    Log.LogWarning("Nav snapshot error: " + ex.Message);
                }
            }

            // Full state snapshot — slower interval (crew, ship, events)
            _frameCounter++;
            if (_frameCounter < _snapshotInterval) return;
            _frameCounter = 0;

            try
            {
                SnapshotState();
            }
            catch (Exception ex)
            {
                Log.LogWarning("Snapshot error: " + ex.Message);
            }
        }

        private void SnapshotState()
        {
            var sb = new StringBuilder(4096);

            // === STATUS ===
            sb.Length = 0;
            sb.Append("{\"version\":\"1.0.0\"");
            sb.Append(",\"gameTime\":").Append(CrewSim.fTotalGameSec.ToString("F1"));
            sb.Append(",\"realTime\":\"").Append(DateTime.Now.ToString("HH:mm:ss")).Append("\"");
            sb.Append(",\"fps\":").Append(Mathf.RoundToInt(1f / Time.unscaledDeltaTime));

            int crewCount = 0;
            var company = CrewSim.coPlayer?.Company;
            List<CondOwner> crewList = null;
            if (company != null)
            {
                crewList = company.GetCrewMembers();
                crewCount = crewList != null ? crewList.Count : 0;
            }

            sb.Append(",\"crewCount\":").Append(crewCount);

            var ship = CrewSim.shipCurrentLoaded;
            sb.Append(",\"shipName\":\"").Append(EscapeJson(ship != null ? ship.publicName ?? ship.strRegID ?? "" : "")).Append("\"");
            sb.Append("}");

            string statusJson = sb.ToString();

            // === CREW ===
            sb.Length = 0;
            sb.Append("{\"timestamp\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\"");
            sb.Append(",\"gameTime\":").Append(CrewSim.fTotalGameSec.ToString("F1"));
            sb.Append(",\"crew\":[");

            if (crewList != null)
            {
                for (int i = 0; i < crewList.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    SerializeCrew(sb, crewList[i]);
                }
            }
            sb.Append("]}");
            string crewJson = sb.ToString();

            // === SHIP ===
            sb.Length = 0;
            SerializeShip(sb, ship);
            string shipJson = sb.ToString();

            // === EVENTS ===
            string eventsJson;
            lock (_eventLock)
            {
                sb.Length = 0;
                sb.Append("[");
                for (int i = 0; i < _eventLog.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.Append(_eventLog[i]); // Already JSON-formatted
                }
                sb.Append("]");
                eventsJson = sb.ToString();
            }

            // Write all caches under single lock
            lock (_cacheLock)
            {
                _statusJson = statusJson;
                _crewJson = crewJson;
                _shipJson = shipJson;
                _eventsJson = eventsJson;
            }
        }

        private string SnapshotNav(StringBuilder sb)
        {
            sb.Append("{");

            // Game epoch (session timer)
            sb.Append("\"epoch\":").Append(CrewSim.fTotalGameSec.ToString("F1"));
            // Orbital epoch — the actual time value used by BodyOrbit.UpdateTime() for Kepler computation
            sb.Append(",\"orbitEpoch\":").Append(StarSystem.fEpoch.ToString("G17"));

            // === PLAYER SHIP ===
            var playerShip = CrewSim.coPlayer?.ship;
            sb.Append(",\"playerShip\":");
            if (playerShip != null && playerShip.objSS != null)
            {
                ShipSitu ps = playerShip.objSS;
                sb.Append("{\"regId\":\"").Append(EscapeJson(playerShip.strRegID ?? "")).Append("\"");
                sb.Append(",\"name\":\"").Append(EscapeJson(playerShip.publicName ?? playerShip.strRegID ?? "")).Append("\"");
                sb.Append(",\"posX\":").Append(ps.vPosx.ToString("G12"));
                sb.Append(",\"posY\":").Append(ps.vPosy.ToString("G12"));
                sb.Append(",\"velX\":").Append(ps.vVelX.ToString("G12"));
                sb.Append(",\"velY\":").Append(ps.vVelY.ToString("G12"));
                sb.Append(",\"heading\":").Append(ps.fRot.ToString("F4"));
                sb.Append(",\"angVel\":").Append(ps.fW.ToString("F4"));
                sb.Append(",\"rcsFuel\":").Append(playerShip.GetRCSRemain().ToString("F2"));
                sb.Append(",\"rcsMax\":").Append(playerShip.GetRCSMax().ToString("F2"));
                try
                {
                    double dv = playerShip.DeltaVRemainingRCS * 149597870700.0;
                    double accel = playerShip.RCSAccelMax;
                    sb.Append(",\"deltaV\":").Append(double.IsNaN(dv) || double.IsInfinity(dv) ? "0" : dv.ToString("F1"));
                    sb.Append(",\"rcsAccel\":").Append(double.IsNaN(accel) || double.IsInfinity(accel) ? "0" : accel.ToString("G8"));
                }
                catch
                {
                    sb.Append(",\"deltaV\":0,\"rcsAccel\":0");
                }
                sb.Append(",\"docked\":").Append(playerShip.bDocked ? "true" : "false");
                sb.Append(",\"size\":").Append(ps.Size);
                sb.Append("}");
            }
            else
            {
                sb.Append("null");
            }

            // === ALL SHIPS IN SYSTEM ===
            sb.Append(",\"ships\":[");
            if (CrewSim.system != null && CrewSim.system.dictShips != null)
            {
                bool first = true;
                int shipCount = 0;
                foreach (var kvp in CrewSim.system.dictShips)
                {
                    Ship s = kvp.Value;
                    if (s == null || s.bDestroyed) continue;
                    if (s == playerShip) continue; // Already in playerShip
                    if (s.objSS == null) continue;
                    if (shipCount >= 150) break; // Cap for payload size

                    if (!first) sb.Append(",");
                    first = false;
                    shipCount++;

                    ShipSitu ss = s.objSS;
                    sb.Append("{\"regId\":\"").Append(EscapeJson(s.strRegID ?? "")).Append("\"");
                    sb.Append(",\"name\":\"").Append(EscapeJson(s.publicName ?? s.strRegID ?? "")).Append("\"");
                    sb.Append(",\"posX\":").Append(ss.vPosx.ToString("G12"));
                    sb.Append(",\"posY\":").Append(ss.vPosy.ToString("G12"));
                    sb.Append(",\"velX\":").Append(ss.vVelX.ToString("G12"));
                    sb.Append(",\"velY\":").Append(ss.vVelY.ToString("G12"));
                    sb.Append(",\"heading\":").Append(ss.fRot.ToString("F4"));
                    sb.Append(",\"type\":\"").Append(s.Classification.ToString()).Append("\"");
                    sb.Append(",\"docked\":").Append(s.bDocked ? "true" : "false");
                    sb.Append(",\"size\":").Append(ss.Size);
                    sb.Append("}");
                }
            }
            sb.Append("]");

            // === ORBITAL BODIES ===
            sb.Append(",\"bodies\":[");
            if (CrewSim.system != null && CrewSim.system.aBOs != null)
            {
                bool first = true;
                foreach (var kvp in CrewSim.system.aBOs)
                {
                    BodyOrbit bo = kvp.Value;
                    if (bo == null) continue;

                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("{\"name\":\"").Append(EscapeJson(bo.strName ?? kvp.Key)).Append("\"");
                    sb.Append(",\"posX\":").Append(bo.dXReal.ToString("G12"));
                    sb.Append(",\"posY\":").Append(bo.dYReal.ToString("G12"));
                    sb.Append(",\"radius\":").Append(bo.fRadius.ToString("G8"));
                    sb.Append(",\"semiMajor\":").Append((bo.fAxis1 / 2.0).ToString("G12"));
                    sb.Append(",\"semiMinor\":").Append((bo.fAxis2 / 2.0).ToString("G12"));
                    sb.Append(",\"ecc\":").Append(bo.fEcc.ToString("G10"));
                    sb.Append(",\"angleDeg\":").Append(bo.fAngle.ToString("G10"));
                    sb.Append(",\"perihelion\":").Append(bo.fPerh.ToString("G12"));
                    sb.Append(",\"period\":").Append(bo.fPeriod.ToString("G12"));
                    sb.Append(",\"periodShift\":").Append(bo.fPeriodShift.ToString("G12"));
                    sb.Append(",\"orbitDir\":").Append(bo.nOrbitDirection);
                    sb.Append(",\"parent\":\"").Append(EscapeJson(bo.boParent != null ? bo.boParent.strName ?? "" : "")).Append("\"");
                    sb.Append("}");
                }
            }
            sb.Append("]");

            // === NAV CONSOLE STATE ===
            sb.Append(",\"navConsole\":");
            try
            {
                if (CrewSim.goUI != null)
                {
                    var od = CrewSim.goUI.GetComponent<GUIOrbitDraw>();
                    if (od != null)
                    {
                        sb.Append("{\"active\":true");

                        var target = GUIOrbitDraw.CrossHairTarget;
                        if (target != null && target.Ship != null)
                        {
                            sb.Append(",\"targetName\":\"").Append(EscapeJson(target.Ship.publicName ?? target.Ship.strRegID ?? "")).Append("\"");
                            sb.Append(",\"targetRegId\":\"").Append(EscapeJson(target.Ship.strRegID ?? "")).Append("\"");
                            sb.Append(",\"targetType\":\"ship\"");
                        }
                        else
                        {
                            sb.Append(",\"targetName\":null,\"targetType\":null");
                        }

                        sb.Append("}");
                    }
                    else
                    {
                        sb.Append("{\"active\":false}");
                    }
                }
                else
                {
                    sb.Append("{\"active\":false}");
                }
            }
            catch
            {
                sb.Append("{\"active\":false}");
            }

            sb.Append("}");
            return sb.ToString();
        }

        private void SerializeCrew(StringBuilder sb, CondOwner co)
        {
            sb.Append("{\"id\":\"").Append(EscapeJson(co.strID ?? "")).Append("\"");
            sb.Append(",\"name\":\"").Append(EscapeJson(co.strNameFriendly ?? co.strName ?? "Unknown")).Append("\"");

            // Key stats
            sb.Append(",\"stats\":{");
            AppendStat(sb, "fatigue", co, "StatFatigue", true);
            AppendStat(sb, "sleep", co, "StatSleep", false);
            AppendStat(sb, "satiety", co, "StatSatiety", false);
            AppendStat(sb, "hydration", co, "StatHydration", false);
            AppendStat(sb, "pain", co, "StatPain", false);
            AppendStat(sb, "hygiene", co, "StatHygiene", false);
            AppendStat(sb, "comfort", co, "StatComfort", false);
            AppendStat(sb, "encumbrance", co, "StatEncumbrance", false);
            AppendStat(sb, "oxygen", co, "StatOxygen", false);
            sb.Append("}");

            // Current action
            string actionName = "";
            string targetName = "";
            if (co.aQueue != null && co.aQueue.Count > 0)
            {
                var ia = co.aQueue[0];
                if (ia != null)
                {
                    actionName = ia.strName ?? ia.strTitle ?? "";
                    if (ia.objThem != null)
                        targetName = ia.objThem.strNameFriendly ?? ia.objThem.strName ?? "";
                }
            }
            sb.Append(",\"currentAction\":\"").Append(EscapeJson(actionName)).Append("\"");
            sb.Append(",\"currentTarget\":\"").Append(EscapeJson(targetName)).Append("\"");
            sb.Append(",\"queueLength\":").Append(co.aQueue != null ? co.aQueue.Count : 0);

            // Shift
            string shiftName = co.jsShiftLast != null ? co.jsShiftLast.strName ?? "" : "";
            sb.Append(",\"shift\":\"").Append(EscapeJson(shiftName)).Append("\"");

            // Key boolean conditions
            sb.Append(",\"isAwake\":").Append(!co.HasCond("IsSleeping") ? "true" : "false");
            sb.Append(",\"isUnconscious\":").Append(co.HasCond("IsUnconscious") ? "true" : "false");
            sb.Append(",\"isAIManual\":").Append(co.HasCond("IsAIManual") ? "true" : "false");

            // Notable conditions (Dc* discomforts + Is* states)
            sb.Append(",\"conditions\":[");
            bool first = true;
            if (co.mapConds != null)
            {
                foreach (var kvp in co.mapConds)
                {
                    string name = kvp.Key;
                    if (name.StartsWith("Dc") || name.StartsWith("Is") ||
                        name == "Gasping" || name == "AttemptingPledgeSurviveO2")
                    {
                        if (!first) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(name)).Append("\"");
                        first = false;
                    }
                }
            }
            sb.Append("]");

            // Priorities (top 5)
            sb.Append(",\"priorities\":[");
            if (_fiPriorities != null && _fiPriorityValue != null && _fiPriorityObjCond != null)
            {
                IList priorities = _fiPriorities.GetValue(co) as IList;
                if (priorities != null)
                {
                    int count = Math.Min(priorities.Count, 5);
                    for (int i = 0; i < count; i++)
                    {
                        if (i > 0) sb.Append(",");
                        object p = priorities[i];
                        double fValue = (double)_fiPriorityValue.GetValue(p);
                        Condition cond = _fiPriorityObjCond.GetValue(p) as Condition;
                        sb.Append("{\"condition\":\"").Append(EscapeJson(cond != null ? cond.strName : "?"));
                        sb.Append("\",\"deficit\":").Append(fValue.ToString("F2")).Append("}");
                    }
                }
            }
            sb.Append("]");

            // Active pledges
            sb.Append(",\"pledges\":[");
            first = true;
            if (co.dictPledges != null)
            {
                foreach (var kvp in co.dictPledges)
                {
                    foreach (var pledge in kvp.Value)
                    {
                        if (!first) sb.Append(",");
                        sb.Append("\"").Append(EscapeJson(pledge.NameFriendly ?? "?")).Append("\"");
                        first = false;
                    }
                }
            }
            sb.Append("]");

            sb.Append("}");
        }

        private void SerializeShip(StringBuilder sb, Ship ship)
        {
            if (ship == null)
            {
                sb.Append("{\"loaded\":false}");
                return;
            }

            sb.Append("{\"loaded\":true");
            sb.Append(",\"name\":\"").Append(EscapeJson(ship.publicName ?? ship.strRegID ?? "")).Append("\"");
            sb.Append(",\"regId\":\"").Append(EscapeJson(ship.strRegID ?? "")).Append("\"");

            // Rooms with atmosphere
            sb.Append(",\"rooms\":[");
            if (ship.aRooms != null)
            {
                bool first = true;
                foreach (var room in ship.aRooms)
                {
                    if (room == null || room.Void) continue;
                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("{\"tiles\":").Append(room.aTiles != null ? room.aTiles.Count : 0);

                    // Room atmosphere via its CondOwner
                    if (room.CO != null)
                    {
                        double ppO2 = room.CO.GetCondAmount("StatGasPpO2");
                        double ppCO2 = room.CO.GetCondAmount("StatGasPpCO2");
                        double pressure = room.CO.GetCondAmount("StatGasPressure");
                        double temp = room.CO.GetCondAmount("StatGasTemp");

                        sb.Append(",\"o2\":").Append(ppO2.ToString("F1"));
                        sb.Append(",\"co2\":").Append(ppCO2.ToString("F2"));
                        sb.Append(",\"pressure\":").Append(pressure.ToString("F0"));
                        sb.Append(",\"temp\":").Append(temp.ToString("F1"));
                    }
                    sb.Append("}");
                }
            }
            sb.Append("]");
            sb.Append("}");
        }

        private void AppendStat(StringBuilder sb, string jsonKey, CondOwner co, string condName, bool isFirst)
        {
            if (!isFirst) sb.Append(",");
            double val = co.HasCond(condName) ? co.GetCondAmount(condName) : 0.0;
            sb.Append("\"").Append(jsonKey).Append("\":").Append(val.ToString("F3"));
        }

        /// <summary>
        /// Add an event to the log. Thread-safe, called from Harmony patches.
        /// </summary>
        internal static void AddEvent(string npcName, string eventType, string detail)
        {
            string json = "{\"time\":\"" + DateTime.Now.ToString("HH:mm:ss") + "\""
                + ",\"npc\":\"" + EscapeJson(npcName) + "\""
                + ",\"type\":\"" + EscapeJson(eventType) + "\""
                + ",\"detail\":\"" + EscapeJson(detail) + "\"}";

            lock (_eventLock)
            {
                _eventLog.Add(json);
                while (_eventLog.Count > _maxEvents)
                    _eventLog.RemoveAt(0);
            }
        }

        private static string EscapeJson(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                    .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }

        private string HandlePostCommand(HttpListenerContext ctx, string path)
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding))
            {
                body = reader.ReadToEnd();
            }

            if (path != "/api/command")
            {
                ctx.Response.StatusCode = 404;
                return "{\"error\":\"POST only supported on /api/command\"}";
            }

            try
            {
                var data = JsonMapper.ToObject(body);
                string action = (string)data["action"];
                string crewId = ((IDictionary)data).Contains("crewId") ? (string)data["crewId"] : null;

                var cmd = new GameCommand
                {
                    Action = action,
                    CrewId = crewId,
                    Data = data,
                    Id = Guid.NewGuid().ToString().Substring(0, 8)
                };

                lock (_cmdLock)
                {
                    _commandQueue.Add(cmd);
                }

                Log.LogInfo("Command queued: " + action + " for " + (crewId ?? "all") + " [" + cmd.Id + "]");
                return "{\"ok\":true,\"cmdId\":\"" + cmd.Id + "\",\"action\":\"" + EscapeJson(action) + "\"}";
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = 400;
                return "{\"error\":\"" + EscapeJson(ex.Message) + "\"}";
            }
        }

        /// <summary>
        /// Execute queued commands on the main thread. Called from Update().
        /// </summary>
        private void ExecuteCommands()
        {
            List<GameCommand> cmds;
            lock (_cmdLock)
            {
                if (_commandQueue.Count == 0) return;
                cmds = new List<GameCommand>(_commandQueue);
                _commandQueue.Clear();
            }

            foreach (var cmd in cmds)
            {
                string result;
                try
                {
                    result = ExecuteCommand(cmd);
                }
                catch (Exception ex)
                {
                    result = "{\"cmdId\":\"" + cmd.Id + "\",\"ok\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}";
                    Log.LogError("Command " + cmd.Id + " failed: " + ex.Message);
                }

                lock (_cmdLock)
                {
                    _commandResults.Add(result);
                    while (_commandResults.Count > 50)
                        _commandResults.RemoveAt(0);
                }
            }
        }

        private string ExecuteCommand(GameCommand cmd)
        {
            CondOwner crew = null;
            if (cmd.CrewId != null)
            {
                if (DataHandler.mapCOs == null || !DataHandler.mapCOs.ContainsKey(cmd.CrewId))
                    return "{\"cmdId\":\"" + cmd.Id + "\",\"ok\":false,\"error\":\"Crew not found: " + EscapeJson(cmd.CrewId) + "\"}";
                crew = DataHandler.mapCOs[cmd.CrewId];
            }

            switch (cmd.Action)
            {
                case "queueInteraction":
                {
                    // Queue a game interaction on a crew member
                    // { action: "queueInteraction", crewId: "...", interactionName: "ACT...", targetId: "..." }
                    string iaName = (string)cmd.Data["interactionName"];
                    Interaction ia = DataHandler.GetInteraction(iaName);
                    if (ia == null)
                        return CmdError(cmd, "Interaction not found: " + iaName);

                    CondOwner target = crew; // default: self-target
                    if (((IDictionary)cmd.Data).Contains("targetId"))
                    {
                        string tid = (string)cmd.Data["targetId"];
                        if (DataHandler.mapCOs.ContainsKey(tid))
                            target = DataHandler.mapCOs[tid];
                        else
                            return CmdError(cmd, "Target not found: " + tid);
                    }

                    bool ok = crew.QueueInteraction(target, ia);
                    string crewName = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(crewName, "Director", "Queued: " + iaName);
                    return CmdOk(cmd, "Queued " + iaName + " on " + crewName + " (accepted=" + ok + ")");
                }

                case "addPledge":
                {
                    // Add a pledge to a crew member
                    // { action: "addPledge", crewId: "...", pledgeName: "AI..." }
                    string pledgeName = (string)cmd.Data["pledgeName"];
                    JsonPledge jp = DataHandler.GetPledge(pledgeName);
                    if (jp == null)
                        return CmdError(cmd, "Pledge not found: " + pledgeName);

                    Pledge2 pledge = PledgeFactory.Factory(crew, jp);
                    if (pledge == null)
                        return CmdError(cmd, "PledgeFactory returned null for: " + pledgeName);

                    crew.AddPledge(pledge);
                    string crewName = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(crewName, "Director", "Added pledge: " + pledgeName);
                    return CmdOk(cmd, "Added pledge " + pledgeName + " to " + crewName);
                }

                case "removePledge":
                {
                    // Remove a pledge by name
                    // { action: "removePledge", crewId: "...", pledgeName: "AI..." }
                    string pledgeName = (string)cmd.Data["pledgeName"];
                    bool found = false;
                    if (crew.dictPledges != null)
                    {
                        foreach (var kvp in crew.dictPledges)
                        {
                            for (int i = kvp.Value.Count - 1; i >= 0; i--)
                            {
                                if (kvp.Value[i].NameFriendly == pledgeName)
                                {
                                    crew.RemovePledge(kvp.Value[i]);
                                    found = true;
                                    break;
                                }
                            }
                            if (found) break;
                        }
                    }
                    string name = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(name, "Director", "Removed pledge: " + pledgeName);
                    return found ? CmdOk(cmd, "Removed " + pledgeName) : CmdError(cmd, "Pledge not found on crew");
                }

                case "setCondition":
                {
                    // Set a condition value on a crew member
                    // { action: "setCondition", crewId: "...", condName: "Stat...", value: 0.5 }
                    string condName = (string)cmd.Data["condName"];
                    double value = (double)cmd.Data["value"];
                    double current = crew.GetCondAmount(condName);
                    double delta = value - current;
                    crew.AddCondAmount(condName, delta);
                    string crewName = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(crewName, "Director", condName + " = " + value.ToString("F2"));
                    return CmdOk(cmd, "Set " + condName + " to " + value.ToString("F2") + " on " + crewName);
                }

                case "boostPriority":
                {
                    // Temporarily boost a condition's deficit to make AI prioritize it
                    // { action: "boostPriority", crewId: "...", condName: "Stat...", amount: -5.0 }
                    string condName = (string)cmd.Data["condName"];
                    double amount = (double)cmd.Data["amount"];
                    // Nudge the stat to create a larger deficit
                    crew.AddCondAmount(condName, amount);
                    string crewName = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(crewName, "Director", "Boosted " + condName + " by " + amount.ToString("F2"));
                    return CmdOk(cmd, "Boosted " + condName + " by " + amount.ToString("F2"));
                }

                case "clearQueue":
                {
                    // Clear a crew member's interaction queue
                    // { action: "clearQueue", crewId: "..." }
                    if (crew.aQueue != null)
                        crew.aQueue.Clear();
                    string crewName = crew.strNameFriendly ?? crew.strName ?? cmd.CrewId;
                    AddEvent(crewName, "Director", "Queue cleared");
                    return CmdOk(cmd, "Cleared queue for " + crewName);
                }

                default:
                    return CmdError(cmd, "Unknown action: " + cmd.Action);
            }
        }

        private string CmdOk(GameCommand cmd, string msg)
        {
            return "{\"cmdId\":\"" + cmd.Id + "\",\"ok\":true,\"msg\":\"" + EscapeJson(msg) + "\"}";
        }

        private string CmdError(GameCommand cmd, string msg)
        {
            return "{\"cmdId\":\"" + cmd.Id + "\",\"ok\":false,\"error\":\"" + EscapeJson(msg) + "\"}";
        }

        private void OnDestroy()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }
            Log.LogInfo("Companion Server stopped.");
        }
    }

    /// <summary>
    /// Log AI events when crew selects work or idle actions.
    /// </summary>
    [HarmonyPatch(typeof(CondOwner), "GetWork")]
    public static class GetWorkEventPatch
    {
        [HarmonyPostfix]
        static void Postfix(CondOwner __instance)
        {
            try
            {
                if (__instance.HasCond("IsPlayer")) return;
                if (CrewSim.coPlayer == null || __instance.Company != CrewSim.coPlayer.Company) return;

                string name = __instance.strNameFriendly ?? __instance.strName ?? __instance.strID;
                if (__instance.aQueue != null && __instance.aQueue.Count > 0)
                {
                    var ia = __instance.aQueue[0];
                    string action = ia != null ? ia.strName ?? ia.strTitle ?? "?" : "?";
                    Plugin.AddEvent(name, "GetWork", action);
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// A command queued from the HTTP API for main-thread execution.
    /// </summary>
    internal class GameCommand
    {
        public string Id;
        public string Action;
        public string CrewId;
        public JsonData Data;
    }

    [HarmonyPatch(typeof(CondOwner), "GetMove2")]
    public static class GetMove2EventPatch
    {
        [HarmonyPostfix]
        static void Postfix(CondOwner __instance)
        {
            try
            {
                if (__instance.HasCond("IsPlayer")) return;
                if (CrewSim.coPlayer == null || __instance.Company != CrewSim.coPlayer.Company) return;

                string name = __instance.strNameFriendly ?? __instance.strName ?? __instance.strID;
                if (__instance.aQueue != null && __instance.aQueue.Count > 0)
                {
                    var ia = __instance.aQueue[0];
                    string action = ia != null ? ia.strName ?? ia.strTitle ?? "?" : "?";
                    string target = "";
                    if (ia != null && ia.objThem != null)
                        target = " -> " + (ia.objThem.strNameFriendly ?? ia.objThem.strName ?? "?");
                    Plugin.AddEvent(name, "GetMove2", action + target);
                }
            }
            catch { }
        }
    }
}
