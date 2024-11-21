using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Linq;
using System.ComponentModel;
using vrcosc_magicchatbox.ViewModels;
using System.Windows;
using vrcosc_magicchatbox.Classes.DataAndSecurity;
using vrcosc_magicchatbox.DataAndSecurity;
using CommunityToolkit.Mvvm.ComponentModel;
using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using vrcosc_magicchatbox.ViewModels.Models;
using HeartRate;
using System.Net.Sockets;

namespace vrcosc_magicchatbox.Classes.Modules
{
    public partial class PulsoidModuleSettings : ObservableObject
    {
        private const string SettingsFileName = "PulsoidModuleSettings.json";

        [ObservableProperty]
        private List<PulsoidTrendSymbolSet> pulsoidTrendSymbols = new();

        [ObservableProperty]
        private int currentHeartIconIndex = 0;

        [ObservableProperty]
        private string heartRateTrendIndicator = string.Empty;

        [ObservableProperty]
        private bool enableHeartRateOfflineCheck = true;

        [ObservableProperty]
        private int unchangedHeartRateTimeoutInSec = 30;

        [ObservableProperty]
        private bool smoothHeartRate = true;

        [ObservableProperty]
        private int smoothHeartRateTimeSpan = 4;

        [ObservableProperty]
        private bool showHeartRateTrendIndicator = true;

        [ObservableProperty]
        private int heartRateTrendIndicatorSampleRate = 5;

        [ObservableProperty]
        private double heartRateTrendIndicatorSensitivity = 0.65;

        [ObservableProperty]
        private bool hideCurrentHeartRate = false;

        [ObservableProperty]
        private bool showTemperatureText = true;

        [ObservableProperty]
        private bool magicHeartRateIcons = true;

        [ObservableProperty]
        private bool magicHeartIconPrefix = true;

        [ObservableProperty]
        private List<string> heartIcons = new List<string> { "❤️", "💖", "💗", "💙", "💚", "💛", "💜" };

        [ObservableProperty]
        private string heartRateIcon = "❤️";

        [ObservableProperty]
        private bool separateTitleWithEnter = false;

        [ObservableProperty]
        private int lowTemperatureThreshold = 60;

        [ObservableProperty]
        private int highTemperatureThreshold = 100;

        [ObservableProperty]
        private bool applyHeartRateAdjustment = false;

        [ObservableProperty]
        private int heartRateAdjustment = -5;

        [ObservableProperty]
        private int heartRateScanInterval = 1;

        [ObservableProperty]
        private string lowHeartRateText = "sleepy";

        [ObservableProperty]
        private string highHeartRateText = "hot";

        [ObservableProperty]
        private bool showBPMSuffix = false;

        [ObservableProperty]
        private string currentHeartRateTitle = "Heart Rate";

        [ObservableProperty]
        private bool heartRateTitle = false;

        [ObservableProperty]
        private PulsoidTrendSymbolSet selectedPulsoidTrendSymbol = new();

        [ObservableProperty]
        private StatisticsTimeRange selectedStatisticsTimeRange = StatisticsTimeRange._24h;

        [ObservableProperty]
        private List<StatisticsTimeRange> statisticsTimeRanges = new();

        [ObservableProperty]
        bool pulsoidStatsEnabled = true;

        [ObservableProperty]
        bool showCalories = false;

        [ObservableProperty]
        bool showAverageHeartRate = true;

        [ObservableProperty]
        bool showMinimumHeartRate = true;

        [ObservableProperty]
        bool showMaximumHeartRate = true;

        [ObservableProperty]
        bool showDuration = false;

        [ObservableProperty]
        bool showStatsTimeRange = false;

        [ObservableProperty]
        bool trendIndicatorBehindStats = true;

        public void SaveSettings()
        {
            var settingsJson = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(GetFullSettingsPath(), settingsJson);
        }

        public static string GetFullSettingsPath()
        {
            return Path.Combine(ViewModel.Instance.DataPath, SettingsFileName);
        }

        public static PulsoidModuleSettings LoadSettings()
        {
            var settingsPath = GetFullSettingsPath();

            if (File.Exists(settingsPath))
            {
                string settingsJson = File.ReadAllText(settingsPath);

                if (string.IsNullOrWhiteSpace(settingsJson) || settingsJson.All(c => c == '\0'))
                {
                    Logging.WriteInfo("he settings JSON file is empty or corrupted.");
                    return new PulsoidModuleSettings();
                }

                try
                {
                    var settings = JsonConvert.DeserializeObject<PulsoidModuleSettings>(settingsJson);

                    if (settings != null)
                    {
                        return settings;
                    }
                    else
                    {
                        Logging.WriteInfo("Failed to deserialize the settings JSON.");
                        return new PulsoidModuleSettings();
                    }
                }
                catch (JsonException ex)
                {
                    Logging.WriteInfo($"Error parsing settings JSON: {ex.Message}");
                    return new PulsoidModuleSettings();
                }
            }
            else
            {
                Logging.WriteInfo("Settings file does not exist, returning new settings instance.");
                return new PulsoidModuleSettings();
            }
        }
    }

    public enum StatisticsTimeRange
    {
        [Description("24h")]
        _24h,
        [Description("7d")]
        _7d,
        [Description("30d")]
        _30d
    }

    public class HeartRateData
    {
        public DateTime MeasuredAt { get; set; }
        public int HeartRate { get; set; }
    }

    public partial class PulsoidStatisticsResponse
    {

        public int maximum_beats_per_minute { get; set; } = 0;
        public int minimum_beats_per_minute { get; set; } = 0;
        public int average_beats_per_minute { get; set; } = 0;
        public int streamed_duration_in_seconds { get; set; } = 0;
        public int calories_burned_in_kcal { get; set; } = 0;
    }

    public partial class PulsoidModule : ObservableObject
    {
        private bool isMonitoringStarted = false;
        private ClientWebSocket _webSocket;
        private CancellationTokenSource _cts;
        private readonly Queue<Tuple<DateTime, int>> _heartRates = new();
        private readonly Queue<int> _heartRateHistory = new();
        private int HeartRateFromSocket = 0;
        private System.Timers.Timer _processDataTimer;
        private int _previousHeartRate = -1;
        private int _unchangedHeartRateCount = 0;
        public PulsoidStatisticsResponse PulsoidStatistics;
        private HttpClient _StatisticsClient = new HttpClient();
        private readonly object _fetchLock = new object();
        private bool _isFetchingStatistics = false;

        [ObservableProperty]
        private int heartRate;

        [ObservableProperty]
        private bool pulsoidDeviceOnline = false;

        [ObservableProperty]
        private DateTime heartRateLastUpdate = DateTime.Now;

        [ObservableProperty]
        private string formattedLowHeartRateText;

        [ObservableProperty]
        private string formattedHighHeartRateText;

        [ObservableProperty]
        private bool pulsoidAccessError = false;

        [ObservableProperty]
        private string pulsoidAccessErrorTxt = string.Empty;



        [ObservableProperty]
        public PulsoidModuleSettings settings;
        private IHeartRateService? heartRateService = null;

        public PulsoidModule()
        {
            Settings = PulsoidModuleSettings.LoadSettings();
            RefreshTrendSymbols();
            RefreshTimeRanges();

            _processDataTimer = new System.Timers.Timer
            {
                AutoReset = true,
                Interval = 1000
            };
            _processDataTimer.Elapsed += (sender, e) =>
            {
                if (Application.Current != null)
                {
                    Application.Current.Dispatcher.Invoke(ProcessData);
                }
            };

            CheckMonitoringConditions();
        }

        public void OnApplicationClosing()
        {
            Settings.SaveSettings();
        }

        public void RefreshTrendSymbols()
        {
            Settings.PulsoidTrendSymbols = new List<PulsoidTrendSymbolSet>
            {
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "↑", DownwardTrendSymbol = "↓" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "⤴️", DownwardTrendSymbol = "⤵️" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "⬆", DownwardTrendSymbol = "⬇" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "↗", DownwardTrendSymbol = "↘" },
                new PulsoidTrendSymbolSet { UpwardTrendSymbol = "🔺", DownwardTrendSymbol = "🔻" },
            };

            var symbolExists = Settings.PulsoidTrendSymbols.Any(s => s.CombinedTrendSymbol == Settings.SelectedPulsoidTrendSymbol.CombinedTrendSymbol);

            if (symbolExists)
            {
                Settings.SelectedPulsoidTrendSymbol = Settings.PulsoidTrendSymbols.FirstOrDefault(s => s.CombinedTrendSymbol == Settings.SelectedPulsoidTrendSymbol.CombinedTrendSymbol);
            }
            else
            {
                Settings.SelectedPulsoidTrendSymbol = Settings.PulsoidTrendSymbols.FirstOrDefault();
            }
        }

        public void RefreshTimeRanges()
        {
            Settings.StatisticsTimeRanges = new List<StatisticsTimeRange>
            {
                StatisticsTimeRange._24h,
                StatisticsTimeRange._7d,
                StatisticsTimeRange._30d
            };
            var rangeExists = Settings.StatisticsTimeRanges.Any(r => r == Settings.SelectedStatisticsTimeRange);
            if (!rangeExists)
            {
                Settings.SelectedStatisticsTimeRange = Settings.StatisticsTimeRanges.FirstOrDefault();
            }
        }

        private static double CalculateSlope(Queue<int> values)
        {
            int count = values.Count;
            double avgX = count / 2.0;
            double avgY = values.Average();

            double sumXY = 0;
            double sumXX = 0;

            for (int i = 0; i < count; i++)
            {
                sumXY += (i - avgX) * (values.ElementAt(i) - avgY);
                sumXX += Math.Pow(i - avgX, 2);
            }

            double slope = sumXY / sumXX;
            return slope;
        }

        public void UpdateFormattedHeartRateText()
        {
            FormattedLowHeartRateText = DataController.TransformToSuperscript(Settings.LowHeartRateText);
            FormattedHighHeartRateText = DataController.TransformToSuperscript(Settings.HighHeartRateText);
        }

        public void PropertyChangedHandler(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Settings.HeartRateScanInterval))
            {
                _processDataTimer.Interval = Settings.HeartRateScanInterval * 1000;
                return;
            }

            if (IsRelevantPropertyChange(e.PropertyName))
            {
                CheckMonitoringConditions();
            }
        }

        private void CheckMonitoringConditions()
        {
            if (ShouldStartMonitoring() && !isMonitoringStarted)
            {
                StartMonitoringHeartRateAsync();
            }
            else if (!ShouldStartMonitoring())
            {
                StopMonitoringHeartRateAsync();
            }
        }

        public bool ShouldStartMonitoring()
        {
            return ViewModel.Instance.IntgrHeartRate && ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrHeartRate_VR ||
                   ViewModel.Instance.IntgrHeartRate && !ViewModel.Instance.IsVRRunning && ViewModel.Instance.IntgrHeartRate_DESKTOP;
        }

        public bool IsRelevantPropertyChange(string propertyName)
        {
            return propertyName == nameof(ViewModel.Instance.IntgrHeartRate) ||
                   propertyName == nameof(ViewModel.Instance.IsVRRunning) ||
                   propertyName == nameof(ViewModel.Instance.IntgrHeartRate_VR) ||
                   propertyName == nameof(ViewModel.Instance.IntgrHeartRate_DESKTOP) ||
                   propertyName == nameof(ViewModel.Instance.PulsoidAccessTokenOAuthEncrypted) || propertyName == nameof(ViewModel.Instance.PulsoidAuthConnected) || propertyName == nameof(ViewModel.Instance.PulsoidAccessTokenOAuth);
        }


        private void StopMonitoringHeartRateAsync()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_webSocket != null && _webSocket.State == WebSocketState.Open)
            {
                _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None).Wait();
                _webSocket.Dispose();
                _webSocket = null;
            }

            if (_processDataTimer.Enabled) {
                _processDataTimer.Stop();
            }

            heartRateService?.Dispose();
            heartRateService = null;

            isMonitoringStarted = false;

            ViewModel.Instance.PulsoidAuthConnected = false;
        }

        public void DisconnectSession()
        { StopMonitoringHeartRateAsync(); }

        private async void StartMonitoringHeartRateAsync()
        {
            if (_cts != null || isMonitoringStarted) return;
            ViewModel.Instance.PulsoidAuthConnected = true;

            isMonitoringStarted = true;
            
            if (!_processDataTimer.Enabled)
                _processDataTimer.Start();

            _cts = new CancellationTokenSource();
            UpdateFormattedHeartRateText();

            if (heartRateService == null)
            {
                heartRateService = Environment.CommandLine.Contains("--test") ? new TestHeartRateService() : new HeartRateService();
                string settingsFilename = HeartRateSettings.GetFilename();


                var watchdog = new HeartRateServiceWatchdog(TimeSpan.FromSeconds(10), heartRateService);

                heartRateService.HeartRateUpdated += Service_HeartRateUpdated;
                await Task.Factory.StartNew(heartRateService.InitiateDefault);
            }
            
            // await ConnectToWebSocketAsync(accessToken, _cts.Token);
        }


        private void Service_HeartRateUpdated(HeartRateReading reading)
        {
            try
            {
                Service_HeartRateUpdatedCore(reading);
            }
            catch (Exception ex)
            {
                DebugLog.WriteLog($"Exception in Service_HeartRateUpdated {ex}");

                Debugger.Break();
            }
        }


        private void Service_HeartRateUpdatedCore(HeartRateReading reading)
        {
            var bpm = reading.BeatsPerMinute;
            var status = reading.Status;

            var isDisconnected = bpm == 0 ||
            status == ContactSensorStatus.NoContact;

            if (reading.IsError || isDisconnected)
            {
                HeartRateFromSocket = 0;
                HeartRateLastUpdate = DateTime.Now;
            }
            else
            {
                HeartRateFromSocket = Settings.ApplyHeartRateAdjustment ? bpm + Settings.HeartRateAdjustment : bpm;
                HeartRateLastUpdate = DateTime.Now;

                Logging.WriteException(new Exception(reading.Error), MSGBox: false);
            }

        }

        static async Task SendBPM(int bpm)
        {
            string url = "http://localhost:8080";

            using (HttpClient client = new HttpClient())
            {
                try
                {
                    // Convert the integer to a string and create the content to send (plain text)
                    StringContent content = new StringContent(bpm.ToString(), Encoding.UTF8, "text/plain");

                    // Send the POST request
                    HttpResponseMessage response = await client.PostAsync(url, content);

                    // Ensure the response was successful
                    response.EnsureSuccessStatusCode();

                    // Read and display the response content
                    string responseBody = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response: {responseBody}");
                }
                catch (SocketException)
                {
                    // Do nothing if the request fails
                }
                catch (HttpRequestException)
                {
                    // Do nothing if the request fails
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                }
            }
        }

        private int ParseHeartRateFromMessage(string message)
        {
            try
            {
                var json = JsonConvert.DeserializeObject<dynamic>(message);
                return json.data.heart_rate;
            }
            catch (Exception ex)
            {
                Logging.WriteException(ex, MSGBox: false);
                return -1;
            }
        }

        public void ProcessData()
        {
            if (HeartRateFromSocket <= 0)
            {
                PulsoidDeviceOnline = false;
                return;
            }
            else
            {
                PulsoidDeviceOnline = true;
            }

            int heartRate = HeartRateFromSocket;

            //if (Settings.PulsoidStatsEnabled)
            //    _ = Task.Run(() => FetchPulsoidStatisticsAsync(ViewModel.Instance.PulsoidAccessTokenOAuth));

            // New logic to handle unchanged heart rate readings
            if (heartRate == _previousHeartRate)
            {
                _unchangedHeartRateCount++;
            }
            else
            {
                _unchangedHeartRateCount = 0; // Reset if the heart rate has changed
                _previousHeartRate = heartRate; // Update previous heart rate
            }

            // Determine if the Pulsoid device should be considered offline
            if (Settings.EnableHeartRateOfflineCheck && _unchangedHeartRateCount >= Settings.UnchangedHeartRateTimeoutInSec)
            {
                PulsoidDeviceOnline = false; // Set the device as offline
                return;
            }
            else
            {
                PulsoidDeviceOnline = true; // Otherwise, consider it online

            }

            // If SmoothHeartRate_v1 is true, calculate and use average heart rate
            if (Settings.SmoothHeartRate)
            {
                // Record the heart rate with the current time
                _heartRates.Enqueue(new Tuple<DateTime, int>(DateTime.UtcNow, heartRate));

                // Remove old data
                while (_heartRates.Count > 0 && DateTime.UtcNow - _heartRates.Peek().Item1 > TimeSpan.FromSeconds(Settings.SmoothHeartRateTimeSpan))
                {
                    _heartRates.Dequeue();
                }

                // Calculate average heart rate over the defined timespan
                heartRate = (int)_heartRates.Average(t => t.Item2);
            }

            // Record the heart rate for trend analysis
            if (Settings.ShowHeartRateTrendIndicator)
            {
                // Only keep the last N heart rates, where N is HeartRateTrendIndicatorSampleRate
                if (_heartRateHistory.Count >= Settings.HeartRateTrendIndicatorSampleRate)
                {
                    _heartRateHistory.Dequeue();
                }

                _heartRateHistory.Enqueue(heartRate);

                // Update the trend indicator
                if (_heartRateHistory.Count > 1)
                {

                    double slope = CalculateSlope(_heartRateHistory);
                    if (slope > Settings.HeartRateTrendIndicatorSensitivity)
                    {
                        Settings.HeartRateTrendIndicator = Settings.SelectedPulsoidTrendSymbol.UpwardTrendSymbol;
                    }
                    else if (slope < -Settings.HeartRateTrendIndicatorSensitivity)
                    {
                        Settings.HeartRateTrendIndicator = Settings.SelectedPulsoidTrendSymbol.DownwardTrendSymbol;
                    }
                    else
                    {
                        Settings.HeartRateTrendIndicator = "";
                    }
                }
            }
            // Update the heart rate icon
            if (Settings.MagicHeartRateIcons)
            {
                // Always cycle through heart icons
                Settings.HeartRateIcon = Settings.HeartIcons[Settings.CurrentHeartIconIndex];
                Settings.CurrentHeartIconIndex = (Settings.CurrentHeartIconIndex + 1) % Settings.HeartIcons.Count;
            }
            // Append additional icons based on heart rate, if the toggle is enabled
            if (Settings.ShowTemperatureText)
            {
                if (heartRate < Settings.LowTemperatureThreshold)
                {
                    Settings.HeartRateIcon = Settings.HeartIcons[Settings.CurrentHeartIconIndex] + FormattedLowHeartRateText;
                }
                else if (heartRate >= Settings.HighTemperatureThreshold)
                {
                    Settings.HeartRateIcon = Settings.HeartIcons[Settings.CurrentHeartIconIndex] + FormattedHighHeartRateText;
                }
            }
            else
                Settings.HeartRateIcon = Settings.HeartIcons[Settings.CurrentHeartIconIndex];

            if (HeartRate != heartRate)
            {
                HeartRate = heartRate;
                _ = SendBPM(heartRate);
            }
        }

        public string GetHeartRateString()
        {
            if (Settings.EnableHeartRateOfflineCheck && !PulsoidDeviceOnline)
                return string.Empty;

            if (HeartRate <= 0)
                return string.Empty;

            StringBuilder displayTextBuilder = new StringBuilder();

            if (Settings.MagicHeartIconPrefix)
            {
                displayTextBuilder.Append(Settings.HeartRateIcon);
            }

            bool showCurrentHeartRate = !Settings.HideCurrentHeartRate;

            if (showCurrentHeartRate)
            {
                displayTextBuilder.Append(" " + HeartRate.ToString());

                if (Settings.ShowBPMSuffix)
                {
                    displayTextBuilder.Append(" bpm");
                }
            }

            if (Settings.ShowHeartRateTrendIndicator && !Settings.TrendIndicatorBehindStats)
            {
                displayTextBuilder.Append($" {Settings.HeartRateTrendIndicator}");
            }


            if (Settings.ShowHeartRateTrendIndicator && Settings.TrendIndicatorBehindStats)
            {
                displayTextBuilder.Append($" {Settings.HeartRateTrendIndicator}");
            }

            if (Settings.HeartRateTitle)
            {
                string titleSeparator = Settings.SeparateTitleWithEnter ? "\v" : ": ";
                string hrTitle = Settings.CurrentHeartRateTitle + titleSeparator;
                displayTextBuilder.Insert(0, hrTitle);
            }

            return displayTextBuilder.ToString();
        }

    }

    public class PulsoidTrendSymbolSet
    {
        public string UpwardTrendSymbol { get; set; } = "↑";
        public string DownwardTrendSymbol { get; set; } = "↓";
        public string CombinedTrendSymbol => $"{UpwardTrendSymbol} - {DownwardTrendSymbol}";
    }

}
