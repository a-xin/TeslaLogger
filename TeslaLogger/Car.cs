﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml;

namespace TeslaLogger
{
    public class Car
    {
        private TeslaState _currentState = TeslaState.Start;
        internal TeslaState GetCurrentState() { return _currentState; }
        Address lastRacingPoint = null;
        private WebHelper webhelper;

        internal enum TeslaState
        {
            Start,
            Drive,
            Park,
            Charge,
            Sleep,
            WaitForSleep,
            Online,
            GoSleep
        }

        // encapsulate state
        internal WebHelper GetWebHelper() { return webhelper; }
        private DateTime lastCarUsed = DateTime.Now;
        internal DateTime GetLastCarUsed() { return lastCarUsed; }
        private DateTime lastOdometerChanged = DateTime.Now;
        internal DateTime GetLastOdometerChanged() { return lastOdometerChanged; }
        private DateTime lastTryTokenRefresh = DateTime.Now;
        internal DateTime GetLastTryTokenRefresh() { return lastTryTokenRefresh; }
        private string lastSetChargeLimitAddressName = string.Empty;
        internal string GetLastSetChargeLimitAddressName() { return lastSetChargeLimitAddressName; }
        private bool goSleepWithWakeup = false;
        internal bool GetGoSleepWithWakeup() { return goSleepWithWakeup; }
        private double odometerLastTrip;
        internal double GetOdometerLastTrip() { return odometerLastTrip; }
        private bool highFrequencyLogging = false;
        internal bool GetHighFreequencyLogging() { return highFrequencyLogging; }
        private int highFrequencyLoggingTicks = 0;
        internal int GetHighFrequencyLoggingTicks() { return highFrequencyLoggingTicks; }
        private int highFrequencyLoggingTicksLimit = 100;
        internal int GetHighFrequencyLoggingTicksLimit() { return highFrequencyLoggingTicksLimit; }
        private DateTime highFrequencyLoggingUntil = DateTime.Now;
        internal DateTime GetHighFrequencyLoggingUntil() { return highFrequencyLoggingUntil; }
        internal enum HFLMode
        {
            Ticks,
            Time
        }
        private HFLMode highFrequencyLoggingMode = HFLMode.Ticks;
        internal HFLMode GetHighFrequencyLoggingMode() { return highFrequencyLoggingMode; }

        Thread thread;
        bool run = true;
        

        internal string TeslaName;
        internal string TeslaPasswort;
        internal int CarInAccount;
        internal int CarInList;

        static List<Car> allcars = new List<Car>();

        public Car(string TeslaName, string TeslaPasswort, int CarInAccount)
        {
            lock (typeof(Car))
            {
                this.TeslaName = TeslaName;
                this.TeslaPasswort = TeslaPasswort;
                this.CarInAccount = CarInAccount;
                this.CarInList = allcars.Count;
                allcars.Add(this);

                webhelper = new WebHelper(this);

                thread = new Thread(Loop);
                thread.Name = "Car" + CarInList;
                thread.Start();
            }
        }

        private void Loop()
        {
            lock (typeof(Car))
            {
                CheckNewCredentials();

                InitStage3();
            }

            while (run)
            {
                try
                {
                    switch (GetCurrentState())
                    {
                        case TeslaState.Start:
                            HandleState_Start();
                            break;

                        case TeslaState.Online:
                            HandleState_Online();
                            break;

                        case TeslaState.Charge:
                            HandleState_Charge();
                            break;

                        case TeslaState.Sleep:
                            HandleState_Sleep();
                            break;

                        case TeslaState.Drive:
                            lastRacingPoint = HandleState_Drive(lastRacingPoint);
                            break;

                        case TeslaState.GoSleep:
                            HandleState_GoSleep();
                            break;

                        case TeslaState.Park:
                            // this state is currently unused
                            Thread.Sleep(5000);
                            break;

                        case TeslaState.WaitForSleep:
                            // this state is currently unused
                            Thread.Sleep(5000);
                            break;

                        default:
                            Log("Main loop default reached with state: " + GetCurrentState().ToString());
                            break;
                    }

                }
                catch (Exception ex)
                {
                    Logfile.ExceptionWriter(ex, "main loop");
                }
            }
        }

        private void InitStage3()
        {
            if (!webhelper.RestoreToken())
            {
                webhelper.Tesla_token = webhelper.GetTokenAsync().Result;
            }

            if (webhelper.Tesla_token == "NULL")
            {
                ExitTeslaLogger("Tesla_token == NULL");
            }

            LogToken();

            if (DBHelper.DBConnectionstring.Length == 0)
            {
                ExitTeslaLogger("DBHelper.DBConnectionstring.Length == 0");
            }

            if (webhelper.GetVehicles() == "NULL")
            {
                ExitTeslaLogger("wh.GetVehicles() == NULL");
            }

            string online = webhelper.IsOnline().Result;
            Log("Streamingtoken: " + webhelper.Tesla_Streamingtoken);

            if (DBHelper.GetMaxPosid(false) == 0)
            {
                Log("Insert first Pos");
                webhelper.IsDriving(true);
            }

            Log("Country Code: " + DBHelper.UpdateCountryCode());

            DBHelper.GetEconomy_Wh_km(webhelper);
            webhelper.DeleteWakeupFile();
            string carName = webhelper.carSettings.Name;
            if (webhelper.carSettings.Raven)
            {
                carName += " Raven";
            }

            Log("Car: " + carName + " - " + webhelper.carSettings.Wh_TR + " Wh/km");
            double.TryParse(webhelper.carSettings.Wh_TR, out DBHelper.currentJSON.Wh_TR);
            DBHelper.GetLastTrip();
            UpdateTeslalogger.Start(webhelper);
            UpdateTeslalogger.UpdateGrafana(webhelper);

            DBHelper.currentJSON.current_car_version = DBHelper.GetLastCarVersion();

        }

        private void ExitTeslaLogger(string v)
        {
            run = false;
            thread.Abort();
        }

        private void HandleState_GoSleep()
        {
            webhelper.ResetLastChargingState();
            bool KeepSleeping = true;
            int round = 0;

            try
            {
                while (KeepSleeping)
                {
                    round++;
                    Thread.Sleep(1000);
                    if (File.Exists(FileManager.GetWakeupTeslaloggerPath))
                    {

                        if (webhelper.DeleteWakeupFile())
                        {
                            string wakeup = webhelper.Wakeup().Result;
                        }

                        KeepSleeping = false;
                        SetCurrentState(TeslaState.Start);
                        break;
                    }
                    else if (round > 10)
                    {
                        round = 0;

                        if (webhelper.TaskerWakeupfile())
                        {
                            if (webhelper.DeleteWakeupFile())
                            {
                                string wakeup = webhelper.Wakeup().Result;
                            }

                            KeepSleeping = false;
                            SetCurrentState(TeslaState.Start);
                            break;
                        }
                    }

                    if (goSleepWithWakeup)
                    {
                        Tools.EndSleeping(out int stopSleepingHour, out int stopSleepingMinute);

                        if (DateTime.Now.Hour == stopSleepingHour && DateTime.Now.Minute == stopSleepingMinute)
                        {
                            Log("Stop Sleeping Timespan reached!");

                            KeepSleeping = false;
                            SetCurrentState(TeslaState.Start);
                            break;
                        }
                    }
                }
            }
            finally
            {
                Log("Restart communication with Tesla Server! 1");
                DBHelper.currentJSON.current_falling_asleep = false;
                DBHelper.currentJSON.CreateCurrentJSON();
            }
        }

        // sleep for max 5 seconds
        private Address HandleState_Drive(Address lastRacingPoint)
        {
            int t = Environment.TickCount;
            if (webhelper.IsDriving())
            {
                lastCarUsed = DateTime.Now;
                t = ApplicationSettings.Default.SleepPosition - 1000 - (Environment.TickCount - t);

                if (t > 0)
                {
                    Thread.Sleep(t); // alle 5 sek eine positionsmeldung
                }

                if (odometerLastTrip != DBHelper.currentJSON.current_odometer)
                {
                    odometerLastTrip = DBHelper.currentJSON.current_odometer;
                    lastOdometerChanged = DateTime.Now;
                }
                else
                {
                    if (webhelper.IsCharging(true))
                    {
                        Log("Charging during Drive -> Finish Trip!!!");
                        DriveFinished();
                    }
                    else
                    {
                        // Odometer didn't change for 600 seconds 
                        TimeSpan ts = DateTime.Now - lastOdometerChanged;
                        if (ts.TotalSeconds > 600)
                        {
                            Log("Odometer didn't change for 600 seconds  -> Finish Trip!!!");
                            DriveFinished();
                        }
                    }
                }

                if (WebHelper.geofence.RacingMode)
                {
                    Address a = WebHelper.geofence.GetPOI(DBHelper.currentJSON.latitude, DBHelper.currentJSON.longitude);
                    if (a != null)
                    {
                        if (lastRacingPoint == null)
                        {
                            lastRacingPoint = a;
                            Log("RACING MODE: Finish Trip!");
                            DriveFinished();
                        }
                    }
                    else
                    {
                        lastRacingPoint = null;
                    }
                }
            }
            else
            {
                webhelper.IsDriving(true); // insert a last position. Maybe the last one is too old

                DriveFinished();

                ShareData sd = new ShareData(webhelper.TaskerHash);
                sd.SendAllChargingData();
            }

            return lastRacingPoint;
        }

        // if online, switch state and return
        // else sleep 10000
        private void HandleState_Sleep()
        {
            string res = webhelper.IsOnline().Result;

            if (res == "online")
            {
                //Log(res);
                SetCurrentState(TeslaState.Start);

                webhelper.IsDriving(true); // Positionsmeldung in DB für Wechsel
            }
            else
            {
                Thread.Sleep(10000);

                UpdateTeslalogger.CheckForNewVersion();
            }
        }

        // sleep 10000 unless in highFrequencyLogging mode
        private void HandleState_Charge()
        {
            {
                if (!webhelper.IsCharging())
                {
                    SetCurrentState(TeslaState.Start);
                    webhelper.IsDriving(true);
                }
                else
                {
                    lastCarUsed = DateTime.Now;
                    if (IsHighFrequenceLoggingEnabled())
                    {
                        Log("HighFrequencyLogging ...");
                    }
                    else
                    {
                        Thread.Sleep(10000);
                    }

                    //wh.GetCachedRollupData();
                }

            }
        }

        // if car is driving, switch state and return
        // else if car is charging, switch state and return
        // else if KeepOnlineMinAfterUsage is reached, sleep SuspendAPIMinutes minutes
        // else sleep 5000
        private void HandleState_Online()
        {
            {
                //if (webhelper.IsDriving() && DBHelper.currentJSON.current_speed > 0)
                if (webhelper.IsDriving() && (webhelper.GetLastShiftState().Equals("R") || webhelper.GetLastShiftState().Equals("N") || webhelper.GetLastShiftState().Equals("D")))
                {
                    webhelper.ResetLastChargingState();
                    lastCarUsed = DateTime.Now;
                    lastOdometerChanged = DateTime.Now;

                    if (webhelper.scanMyTesla != null)
                    {
                        webhelper.scanMyTesla.FastMode(true);
                    }

                    double missingOdometer = DBHelper.currentJSON.current_odometer - odometerLastTrip;

                    if (odometerLastTrip != 0)
                    {
                        if (missingOdometer > 5)
                        {
                            Log($"Missing: {missingOdometer} km! - Check: https://teslalogger.de/faq-1.php");
                            WriteMissingFile(missingOdometer);
                        }
                        else
                        {
                            Log($"Missing: {missingOdometer} km");
                        }
                    }

                    webhelper.StartStreamThread(); // für altitude
                    DBHelper.StartDriveState();
                    SetCurrentState(TeslaState.Drive);

                    Task.Run(() => webhelper.DeleteWakeupFile());
                    return;
                }
                else if (webhelper.IsCharging(true))
                {
                    lastCarUsed = DateTime.Now;
                    Log("Charging");
                    if (webhelper.scanMyTesla != null)
                    {
                        webhelper.scanMyTesla.FastMode(true);
                    }

                    webhelper.IsDriving(true);
                    DBHelper.StartChargingState(webhelper);
                    SetCurrentState(TeslaState.Charge);

                    webhelper.DeleteWakeupFile();
                }
                else
                {
                    RefreshToken();

                    Tools.StartSleeping(out int startSleepHour, out int startSleepMinute);
                    bool doSleep = true;

                    if (FileManager.CheckCmdGoSleepFile())
                    {
                        Log("STOP communication with Tesla Server to enter sleep Mode! (Sleep Button)  https://teslalogger.de/faq-1.php");
                        SetCurrentState(TeslaState.GoSleep);
                        goSleepWithWakeup = false;
                    }
                    else if (DateTime.Now.Hour == startSleepHour && DateTime.Now.Minute == startSleepMinute)
                    {
                        Log("STOP communication with Tesla Server to enter sleep Mode! (Timespan Sleep Mode)  https://teslalogger.de/faq-1.php");
                        SetCurrentState(TeslaState.GoSleep);
                        goSleepWithWakeup = true;
                    }
                    else
                    {
                        // wenn er 15 min online war und nicht geladen oder gefahren ist, dann muss man ihn die möglichkeit geben offline zu gehen
                        TimeSpan ts = DateTime.Now - lastCarUsed;
                        if (ts.TotalMinutes > ApplicationSettings.Default.KeepOnlineMinAfterUsage)
                        {
                            SetCurrentState(TeslaState.Start);

                            webhelper.IsDriving(true); // kurz bevor er schlafen geht, eine Positionsmeldung speichern und schauen ob standheizung / standklima / sentry läuft.
                            if (DBHelper.currentJSON.current_is_preconditioning)
                            {
                                Log("preconditioning prevents car to get sleep");
                                lastCarUsed = DateTime.Now;
                            }
                            else if (webhelper.is_sentry_mode)
                            {
                                Log("sentry_mode prevents car to get sleep");
                                lastCarUsed = DateTime.Now;
                            }
                            else
                            {
                                try
                                {
                                    Log("STOP communication with Tesla Server to enter sleep Mode! https://teslalogger.de/faq-1.php");
                                    DBHelper.currentJSON.current_falling_asleep = true;
                                    DBHelper.currentJSON.CreateCurrentJSON();

                                    for (int x = 0; x < ApplicationSettings.Default.SuspendAPIMinutes * 10; x++)
                                    {
                                        TimeSpan tsSMT = DateTime.Now - DBHelper.currentJSON.lastScanMyTeslaReceived;
                                        if (DBHelper.currentJSON.SMTSpeed > 5 &&
                                            DBHelper.currentJSON.SMTSpeed < 260 &&
                                            DBHelper.currentJSON.SMTBatteryPower > 2 &&
                                            tsSMT.TotalMinutes < 5)
                                        {
                                            Log("ScanMyTesla prevents car to get sleep. Speed: " + DBHelper.currentJSON.SMTSpeed);
                                            lastCarUsed = DateTime.Now;
                                            string wakeup = webhelper.Wakeup().Result;
                                            doSleep = false;
                                            break;
                                        }

                                        if (webhelper.ExistsWakeupFile)
                                        {
                                            Log("Wakeupfile prevents car to get sleep");
                                            lastCarUsed = DateTime.Now;
                                            webhelper.DeleteWakeupFile();
                                            string wakeup = webhelper.Wakeup().Result;
                                            doSleep = false;
                                            break;
                                        }

                                        if (x % 10 == 0)
                                        {
                                            Log("Waiting for car to go to sleep " + (x / 10).ToString());

                                            Tools.StartSleeping(out startSleepHour, out startSleepMinute);
                                            if (DateTime.Now.Hour == startSleepHour && DateTime.Now.Minute == startSleepMinute)
                                            {
                                                Log("STOP communication with Tesla Server to enter sleep Mode! (Timespan Sleep Mode)  https://teslalogger.de/faq-1.php");
                                                SetCurrentState(TeslaState.GoSleep);
                                                goSleepWithWakeup = true;
                                                break;
                                            }
                                        }

                                        Thread.Sleep(1000 * 6);
                                    }
                                }
                                finally
                                {
                                    if (!goSleepWithWakeup)
                                    {
                                        Log("Restart communication with Tesla Server! 2");
                                        DBHelper.currentJSON.current_falling_asleep = false;
                                        DBHelper.currentJSON.CreateCurrentJSON();
                                    }
                                }
                            }
                        }
                    }

                    DBHelper.currentJSON.CheckCreateCurrentJSON();

                    if (doSleep)
                    {
                        Thread.Sleep(5000);
                    }
                    else
                    {
                        return;
                    }
                }
            }

        }

        // if offline, sleep 30000
        // loop until wackup file or back online, sleep 30000 in loop
        private void HandleState_Start()
        {
            RefreshToken();

            if (webhelper.scanMyTesla != null)
            {
                webhelper.scanMyTesla.FastMode(false);
            }

            // Alle States werden geschlossen
            DBHelper.CloseChargingState();
            DBHelper.CloseDriveState(webhelper.lastIsDriveTimestamp);

            string res = webhelper.IsOnline().Result;
            lastCarUsed = DateTime.Now;
            if (res == "online")
            {
                //Log(res);
                SetCurrentState(TeslaState.Online);
                webhelper.IsDriving(true);
                webhelper.ResetLastChargingState();
                DBHelper.StartState(res);
                return;
            }
            else if (res == "asleep")
            {
                //Log(res);
                SetCurrentState(TeslaState.Sleep);
                DBHelper.StartState(res);
                webhelper.ResetLastChargingState();
                DBHelper.currentJSON.CreateCurrentJSON();
            }
            else if (res == "offline")
            {
                //Log(res);
                DBHelper.StartState(res);
                DBHelper.currentJSON.CreateCurrentJSON();

                while (true)
                {
                    Thread.Sleep(30000);
                    string res2 = webhelper.IsOnline().Result;

                    if (res2 != "offline")
                    {
                        Log("Back Online: " + res2);
                        break;
                    }

                    if (webhelper.TaskerWakeupfile())
                    {
                        if (webhelper.DeleteWakeupFile())
                        {
                            string wakeup = webhelper.Wakeup().Result;
                            lastCarUsed = DateTime.Now;
                        }

                        SetCurrentState(TeslaState.Start);
                        break;
                    }
                }
            }
            else
            {
                DBHelper.currentJSON.current_sleeping = false;
                DBHelper.currentJSON.current_online = false;
                DBHelper.currentJSON.CreateCurrentJSON();

                Log("Unhandled State: " + res);
            }
        }


        private void LogToken()
        {
            // don't show full Token in Logfile
            string tempToken = webhelper.Tesla_token;
            if (tempToken.Length > 5)
            {
                tempToken = tempToken.Substring(0, tempToken.Length - 5);
                tempToken += "XXXXX";
            }

            Log("TOKEN: " + tempToken);
        }

        private void DriveFinished()
        {
            // finish trip
            SetCurrentState(TeslaState.Start);
            DBHelper.currentJSON.current_trip_end = DateTime.Now;
            DBHelper.currentJSON.current_trip_km_end = DBHelper.currentJSON.current_odometer;
            DBHelper.currentJSON.current_trip_end_range = DBHelper.currentJSON.current_ideal_battery_range_km;
            webhelper.StopStreaming();

            odometerLastTrip = DBHelper.currentJSON.current_odometer;
        }


        private void CheckNewCredentials()
        {
            /* TODO
            try
            {
                if (!File.Exists(FileManager.GetFilePath(TLFilename.NewCredentialsFilename)))
                {
                    return;
                }

                Log("new_credentials.json available");

                string json = File.ReadAllText(FileManager.GetFilePath(TLFilename.NewCredentialsFilename));
                dynamic j = new JavaScriptSerializer().DeserializeObject(json);

                XmlDocument doc = new XmlDocument();
                doc.Load(FileManager.GetFilePath(TLFilename.TeslaLoggerExeConfigFilename));
                XmlNodeList nodesTeslaName = doc.SelectNodes("/configuration/applicationSettings/TeslaLogger.ApplicationSettings/setting[@name='TeslaName']/value");
                nodesTeslaName.Item(0).InnerText = j["email"];

                XmlNodeList nodesTeslaPasswort = doc.SelectNodes("/configuration/applicationSettings/TeslaLogger.ApplicationSettings/setting[@name='TeslaPasswort']/value");
                nodesTeslaPasswort.Item(0).InnerText = j["password"];

                doc.Save(FileManager.GetFilePath(TLFilename.TeslaLoggerExeConfigFilename));

                if (File.Exists(FileManager.GetFilePath(TLFilename.TeslaTokenFilename)))
                {
                    File.Delete(FileManager.GetFilePath(TLFilename.TeslaTokenFilename));
                }

                File.Delete(FileManager.GetFilePath(TLFilename.NewCredentialsFilename));

                ApplicationSettings.Default.Reload();

                Log("credentials updated!");
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }

            */
        }

        private void RefreshToken()
        {
            TimeSpan ts = DateTime.Now - webhelper.lastTokenRefresh;
            if (ts.TotalDays > 9)
            {
                // If car wasn't sleeping since 10 days, try to get a new Teslalogger update
                UpdateTeslalogger.CheckForNewVersion();

                TimeSpan ts2 = DateTime.Now - lastTryTokenRefresh;
                if (ts2.TotalMinutes > 30)
                {
                    lastTryTokenRefresh = DateTime.Now;
                    Log("try to get new Token");

                    string temp = webhelper.GetTokenAsync().Result;
                    if (temp != "NULL")
                    {
                        Log("new Token received!");

                        webhelper.Tesla_token = temp;
                        webhelper.lastTokenRefresh = DateTime.Now;

                        // Every 10 Days send degradataion Data
                        ShareData sd = new ShareData(webhelper.TaskerHash);
                        sd.SendDegradationData();
                    }
                    else
                    {
                        Log("Error getting new Token!");
                    }
                }
            }
        }

        public void HandleShiftStateChange(string _oldState, string _newState)
        {
            Log("ShiftStateChange: " + _oldState + " -> " + _newState);
            lastCarUsed = DateTime.Now;
            Address addr = WebHelper.geofence.GetPOI(DBHelper.currentJSON.latitude, DBHelper.currentJSON.longitude, false);
            // process special flags for POI
            if (addr != null && addr.specialFlags != null && addr.specialFlags.Count > 0)
            {
                foreach (KeyValuePair<Address.SpecialFlags, string> flag in addr.specialFlags)
                {
                    switch (flag.Key)
                    {
                        case Address.SpecialFlags.OpenChargePort:
                            HandleSpecialFlag_OpenChargePort(flag.Value, _oldState, _newState);
                            break;
                        case Address.SpecialFlags.HighFrequencyLogging:
                            break;
                        case Address.SpecialFlags.EnableSentryMode:
                            HandleSpeciaFlag_EnableSentryMode(flag.Value, _oldState, _newState);
                            break;
                        case Address.SpecialFlags.SetChargeLimit:
                            break;
                        case Address.SpecialFlags.ClimateOff:
                            HandleSpeciaFlag_ClimateOff(flag.Value, _oldState, _newState);
                            break;
                        default:
                            Log("handleShiftStateChange unhandled special flag " + flag.ToString());
                            break;
                    }
                }
            }
            // execute shift state change actions independant from special flags
            // TODO discuss!
            /*if (_newState.Equals("D") && DBHelper.currentJSON.current_is_sentry_mode)
            {
                Log("DisableSentryMode ...");
                string result = webhelper.PostCommand("command/set_sentry_mode?on=false", null).Result;
                Log("DisableSentryMode(): " + result);
            }*/
        }

        private void HandleSpecialFlag_HighFrequencyLogging(string _flagconfig)
        {
            string pattern = "([0-9]+)([a-z])";
            Match m = Regex.Match(_flagconfig, pattern);
            if (m.Success && m.Groups.Count == 3 && m.Groups[1].Captures.Count == 1 && m.Groups[2].Captures.Count == 1)
            {
                if (m.Groups[1].Captures[0] != null && m.Groups[2].Captures[0] != null && int.TryParse(m.Groups[1].Captures[0].ToString(), out int duration))
                {
                    DateTime until = DateTime.Now;
                    switch (m.Groups[2].Captures[0].ToString())
                    {
                        case "s":
                            until = until.AddSeconds(duration);
                            break;
                        case "m":
                            until = until.AddMinutes(duration);
                            break;
                        case "h":
                            until = until.AddHours(duration);
                            break;
                        case "d":
                            until = until.AddDays(duration);
                            break;
                        default:
                            Log("HandleSpecialFlagHighFrequencyLogging unhandled time parameter: " + m.Groups[1].Captures[0].ToString() + m.Groups[2].Captures[0].ToString());
                            break;
                    }
                    EnableHighFrequencyLoggingMode(HFLMode.Time, 0, until);
                }
            }
            else
            {
                pattern = "([0-9]+)";
                m = Regex.Match(_flagconfig, pattern);
                if (m.Success && m.Groups.Count == 2 && m.Groups[1].Captures.Count == 1)
                {
                    if (int.TryParse(m.Groups[1].Captures[0].ToString(), out int result))
                    {
                        EnableHighFrequencyLoggingMode(HFLMode.Ticks, result, DateTime.Now);
                    }
                }
            }
        }

        private void HandleSpecialFlag_OpenChargePort(string _flagconfig, string _oldState, string _newState)
        {
            string pattern = "([PRND]+)->([PRND]+)";
            Match m = Regex.Match(_flagconfig, pattern);
            if (m.Success && m.Groups.Count == 3 && m.Groups[1].Captures.Count == 1 && m.Groups[2].Captures.Count == 1 && m.Groups[1].Captures[0].ToString().Contains(_oldState) && m.Groups[2].Captures[0].ToString().Contains(_newState))
            {
                Task.Factory.StartNew(() =>
                {
                    Log("OpenChargePort ...");
                    string result = webhelper.PostCommand("command/charge_port_door_open", null).Result;
                    Log("charge_port_door_open(): " + result);
                });
            }
        }

        private void HandleSpeciaFlag_EnableSentryMode(string _flagconfig, string _oldState, string _newState)
        {
            string pattern = "([PRND]+)->([PRND]+)";
            Match m = Regex.Match(_flagconfig, pattern);
            if (m.Success && m.Groups.Count == 3 && m.Groups[1].Captures.Count == 1 && m.Groups[2].Captures.Count == 1 && m.Groups[1].Captures[0].ToString().Contains(_oldState) && m.Groups[2].Captures[0].ToString().Contains(_newState))
            {
                Task.Factory.StartNew(() =>
                {
                    Log("EnableSentryMode ...");
                    string result = webhelper.PostCommand("command/set_sentry_mode", "{\"on\":true}", true).Result;
                    Log("set_sentry_mode(): " + result);
                });
            }
        }

        private void HandleSpeciaFlag_ClimateOff(string _flagconfig, string _oldState, string _newState)
        {
            string pattern = "([PRND]+)->([PRND]+)";
            Match m = Regex.Match(_flagconfig, pattern);
            if (m.Success && m.Groups.Count == 3 && m.Groups[1].Captures.Count == 1 && m.Groups[2].Captures.Count == 1 && m.Groups[1].Captures[0].ToString().Contains(_oldState) && m.Groups[2].Captures[0].ToString().Contains(_newState))
            {
                Task.Factory.StartNew(() =>
                {
                    Log("ClimateOff ...");
                    string result = webhelper.PostCommand("command/auto_conditioning_stop", null).Result;
                    Log("auto_conditioning_stop(): " + result);
                });
            }
        }

        private void HandleSpecialFlag_SetChargeLimit(Address _addr, string _flagconfig)
        {
            string pattern = "([0-9]+)";
            Match m = Regex.Match(_flagconfig, pattern);
            if (m.Success && m.Groups.Count == 2 && m.Groups[1].Captures.Count == 1)
            {
                if (m.Groups[1].Captures[0] != null && int.TryParse(m.Groups[1].Captures[0].ToString(), out int chargelimit))
                {
                    if (!lastSetChargeLimitAddressName.Equals(_addr.name))
                    {
                        Task.Factory.StartNew(() =>
                        {
                            Log($"SetChargeLimit to {chargelimit} ...");
                            string result = webhelper.PostCommand("command/set_charge_limit", "{\"percent\":" + chargelimit + "}", true).Result;
                            Log("set_charge_limit(): " + result);
                            lastSetChargeLimitAddressName = _addr.name;
                        });
                    }
                }
            }
        }

        private void ResetHighFrequencyLogging()
        {
            highFrequencyLogging = false;
            highFrequencyLoggingMode = HFLMode.Ticks;
            highFrequencyLoggingTicks = 0;
            highFrequencyLoggingTicksLimit = 0;
            highFrequencyLoggingUntil = DateTime.Now;
        }

        // do something on state changes
        private void HandleStateChange(TeslaState _oldState, TeslaState _newState)
        {
            Log("change TeslaLogger state: " + _oldState.ToString() + " -> " + _newState.ToString());
            DBHelper.currentJSON.CreateCurrentJSON();

            // any -> Start
            if (_oldState != TeslaState.Start && _newState == TeslaState.Start)
            {
                webhelper.SetLastShiftState("P"); // reset shift state to default "P"
            }
            // charging -> any
            if (_oldState == TeslaState.Charge && _newState != TeslaState.Charge)
            {
                ResetHighFrequencyLogging();
            }
            // sleeping -> any
            if (_oldState == TeslaState.Sleep && _newState != TeslaState.Sleep)
            {
                DBHelper.currentJSON.current_falling_asleep = false;
                DBHelper.currentJSON.CreateCurrentJSON();
            }
            // Start -> Online - Update Car Version after Update
            if (_oldState == TeslaState.Start && _newState == TeslaState.Online)
            {
                _ = webhelper.GetOdometerAsync();
            }
            // any -> charging
            if (_oldState != TeslaState.Charge && _newState == TeslaState.Charge)
            {
                Address addr = WebHelper.geofence.GetPOI(DBHelper.currentJSON.latitude, DBHelper.currentJSON.longitude, false);
                if (addr != null && addr.specialFlags != null && addr.specialFlags.Count > 0)
                {
                    foreach (KeyValuePair<Address.SpecialFlags, string> flag in addr.specialFlags)
                    {
                        switch (flag.Key)
                        {
                            case Address.SpecialFlags.OpenChargePort:
                                break;
                            case Address.SpecialFlags.HighFrequencyLogging:
                                HandleSpecialFlag_HighFrequencyLogging(flag.Value);
                                break;
                            case Address.SpecialFlags.EnableSentryMode:
                                break;
                            case Address.SpecialFlags.SetChargeLimit:
                                HandleSpecialFlag_SetChargeLimit(addr, flag.Value);
                                break;
                            case Address.SpecialFlags.ClimateOff:
                                break;
                            default:
                                Log("handleShiftStateChange unhandled special flag " + flag.ToString());
                                break;
                        }
                    }
                }
            }
        }


        private void SetCurrentState(TeslaState _newState)
        {
            if (_currentState != _newState)
            {
                HandleStateChange(_currentState, _newState);
            }
            _currentState = _newState;
        }


        private void WriteMissingFile(double missingOdometer)
        {
            try
            {
                string filepath = Path.Combine(FileManager.GetExecutingPath(), "MISSINGKM");
                File.AppendAllText(filepath, DateTime.Now.ToString(Tools.ciDeDE) + " : " + $"Missing: {missingOdometer}km!\r\n");

                UpdateTeslalogger.Chmod(filepath, 666, false);
            }
            catch (Exception)
            { }
        }

        public bool IsHighFrequenceLoggingEnabled(bool justcheck = false)
        {
            if (highFrequencyLogging)
            {
                switch (highFrequencyLoggingMode)
                {
                    case HFLMode.Ticks:
                        if (highFrequencyLoggingTicks < highFrequencyLoggingTicksLimit)
                        {
                            if (!justcheck)
                            {
                                highFrequencyLoggingTicks++;
                            }
                            return true;
                        }
                        break;
                    case HFLMode.Time:
                        TimeSpan ts = highFrequencyLoggingUntil - DateTime.Now;
                        if (ts.TotalMilliseconds > 0)
                        {
                            return true;
                        }
                        break;
                    default:
                        break;
                }
            }
            return false;
        }


        private void EnableHighFrequencyLoggingMode(HFLMode _mode, int _ticklimit, DateTime _until)
        {
            Log("enable HighFrequencyLogging - mode: " + _mode.ToString() + (_mode == HFLMode.Ticks ? " ticks: " + _ticklimit : " until: " + _until.ToString()));
            switch (_mode)
            {
                case HFLMode.Ticks:
                    highFrequencyLogging = true;
                    highFrequencyLoggingMode = _mode;
                    highFrequencyLoggingTicksLimit = _ticklimit;
                    break;
                case HFLMode.Time:
                    highFrequencyLogging = true;
                    highFrequencyLoggingMode = _mode;
                    highFrequencyLoggingUntil = _until;
                    break;
                default:
                    Log("EnableHighFrequencyLoggingMode default");
                    break;
            }
        }

        void Log(string text)
        {
            string temp = "#" + CarInList + ": " + text;
            Logfile.Log(temp);
        }
    }
}
