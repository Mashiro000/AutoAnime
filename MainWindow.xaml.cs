#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;
using WinForms = System.Windows.Forms;

// 解决引用冲突
using MediaBrushes = System.Windows.Media.Brushes;

namespace AutoAnime
{
    // --- 转换器 ---
    public class FlexibleIntConverter : JsonConverter<int>
    {
        public override int Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.String)
            {
                var s = r.GetString();
                if (string.IsNullOrWhiteSpace(s)) return 0;
                return int.TryParse(s, out int v) ? v : 0;
            }
            return r.TokenType == JsonTokenType.Number ? r.GetInt32() : 0;
        }
        public override void Write(Utf8JsonWriter w, int v, JsonSerializerOptions o) => w.WriteNumberValue(v);
    }
    public class FlexibleStringConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number) return r.GetInt32().ToString();
            if (r.TokenType == JsonTokenType.String) return r.GetString() ?? "";
            return "";
        }
        public override void Write(Utf8JsonWriter w, string v, JsonSerializerOptions o) => w.WriteStringValue(v);
    }

    // --- 模型 ---
    public class ApiProvider { public string Name { get; set; } = ""; public string Url { get; set; } = ""; public string DefaultModel { get; set; } = ""; }

    public class MediaInfo
    {
        public string? title { get; set; }
        public string? original_title { get; set; }
        public string type { get; set; } = "Anime";

        [JsonConverter(typeof(FlexibleIntConverter))] public int year { get; set; } = 0;
        [JsonConverter(typeof(FlexibleStringConverter))] public string season { get; set; } = "1";
        [JsonConverter(typeof(FlexibleStringConverter))] public string episode { get; set; } = "1";

        public string? episode_title { get; set; }
        [JsonConverter(typeof(FlexibleIntConverter))] public int tmdb_id { get; set; }

        public int GetSeasonInt() => ExtractInt(season);
        public int GetEpisodeInt() => ExtractInt(episode);
        private int ExtractInt(string? input)
        {
            if (string.IsNullOrEmpty(input)) return 1;
            var match = Regex.Match(input, @"\d+");
            return match.Success ? int.Parse(match.Value) : 1;
        }
    }

    public class AiProfile
    {
        public string Remark { get; set; } = "默认配置";
        public string ApiUrl { get; set; } = "";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string TmdbKey { get; set; } = "";
        public int ProviderIndex { get; set; } = 0;
    }

    public class AppSettings
    {
        public string SourcePath { get; set; } = "";
        public string TargetPath { get; set; } = "";
        public bool IsHardLinkMode { get; set; } = true;
        public string AiUrl { get; set; } = "";
        public string AiKey { get; set; } = "";
        public string AiModel { get; set; } = "";
        public string TmdbKey { get; set; } = "";
        public string QbUrl { get; set; } = "";
        public string QbUser { get; set; } = "";
        public string QbPass { get; set; } = "";
        public bool IsDebugMode { get; set; } = false;
        public bool AutoStart { get; set; } = false;
        public List<AiProfile> Profiles { get; set; } = new List<AiProfile>();
        public int LastProfileIndex { get; set; } = -1;
    }

    public partial class MainWindow : FluentWindow
    {
        private FileSystemWatcher? _watcher;
        private bool _isRunning = false;
        private bool _isLoadingProfile = false;
        private bool _isRealExit = false;

        private WinForms.NotifyIcon? _notifyIcon;

        private ConcurrentDictionary<string, MediaInfo> _folderContext = new ConcurrentDictionary<string, MediaInfo>();
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _dirLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        private static readonly CookieContainer _cookies = new CookieContainer();
        private static readonly HttpClient _http = new HttpClient(new HttpClientHandler { UseCookies = true, CookieContainer = _cookies });

        public ObservableCollection<ApiProvider> Providers { get; set; } = new ObservableCollection<ApiProvider>();
        public ObservableCollection<AiProfile> Profiles { get; set; } = new ObservableCollection<AiProfile>();

        [DllImport("Kernel32.dll", CharSet = CharSet.Unicode)]
        static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

        public MainWindow()
        {
            InitializeComponent();
            _http.Timeout = TimeSpan.FromSeconds(60);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            ApplicationThemeManager.ApplySystemTheme();

            InitProviders();
            InitTrayIcon();
            CmbProfiles.ItemsSource = Profiles;
            LoadSettings();
        }

        // --- 辅助：显示 WinUI 弹窗 (替代 MessageBox) ---
        private async Task ShowDialog(string title, string content, string buttonText = "确定")
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = buttonText,
#pragma warning disable CS0618 
                DialogHost = RootContentDialogPresenter
#pragma warning restore CS0618
            };
            await dialog.ShowAsync();
        }

        // --- 系统托盘 ---
        private void InitTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = false,
                Text = "AutoMedia AI - 双击显示"
            };
            _notifyIcon.DoubleClick += (s, e) => {
                Show();
                WindowState = WindowState.Normal;
                _notifyIcon.Visible = false;
            };
        }

        // --- 拦截关闭 ---
        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_isRealExit) return;

            e.Cancel = true;

            var dialog = new ContentDialog
            {
                Title = "关闭程序",
                Content = "您希望将程序最小化到系统托盘继续运行，还是彻底退出？",
                PrimaryButtonText = "最小化到托盘",
                SecondaryButtonText = "彻底退出",
                CloseButtonText = "取消",
#pragma warning disable CS0618 
                DialogHost = RootContentDialogPresenter
#pragma warning restore CS0618
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                Hide();
                if (_notifyIcon != null) _notifyIcon.Visible = true;
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _isRealExit = true;
                if (_notifyIcon != null) _notifyIcon.Dispose();
                Close();
            }
        }

        // --- 开机自启 ---
        private void SwAutoStart_Click(object sender, RoutedEventArgs e)
        {
            string appName = "AutoMediaAI";
            string appPath = Environment.ProcessPath ?? "";
            if (string.IsNullOrEmpty(appPath)) { Log("❌ 无法获取程序路径", true); return; }

            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (SwAutoStart.IsChecked == true) key?.SetValue(appName, $"\"{appPath}\"");
                else key?.DeleteValue(appName, false);

                Log($"⚙️ 开机自启已{(SwAutoStart.IsChecked == true ? "开启" : "关闭")}");
            }
            catch (Exception ex) { Log($"❌ 设置失败: {ex.Message}", true); SwAutoStart.IsChecked = !SwAutoStart.IsChecked; }
        }

        private void InitProviders()
        {
            Providers.Add(new ApiProvider { Name = "🚀 硅基流动 (DeepSeek)", Url = "https://api.siliconflow.cn/v1", DefaultModel = "deepseek-ai/DeepSeek-V3" });
            Providers.Add(new ApiProvider { Name = "🐋 DeepSeek (官方)", Url = "https://api.deepseek.com", DefaultModel = "deepseek-chat" });
            Providers.Add(new ApiProvider { Name = "🥨 豆包 (火山引擎)", Url = "https://ark.cn-beijing.volces.com/api/v3", DefaultModel = "" });
            Providers.Add(new ApiProvider { Name = "☁️ 阿里云 (通义千问)", Url = "https://dashscope.aliyuncs.com/compatible-mode/v1", DefaultModel = "qwen-turbo" });
            Providers.Add(new ApiProvider { Name = "🌟 Google Gemini", Url = "https://generativelanguage.googleapis.com/v1beta/openai", DefaultModel = "gemini-1.5-flash" });
            Providers.Add(new ApiProvider { Name = "🤖 OpenAI (GPT-4o)", Url = "https://api.openai.com/v1", DefaultModel = "gpt-4o" });
            CmbProviders.ItemsSource = Providers;
        }

        private async Task ProcessFile(string filePath)
        {
            if (Directory.Exists(filePath)) return;
            string ext = Path.GetExtension(filePath).ToLower();
            if (ext != ".mp4" && ext != ".mkv" && ext != ".avi" && ext != ".mov" && ext != ".ts") return;

            string parentDir = Path.GetDirectoryName(filePath) ?? "";
            var dirLock = _dirLocks.GetOrAdd(parentDir, _ => new SemaphoreSlim(1, 1));

            int maxRetries = 5;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    await dirLock.WaitAsync();
                    string fileName = Path.GetFileName(filePath);
                    if (i == 0) Log($"🎬 处理文件: {fileName}"); else Log($"🔄 第 {i + 1} 次重试: {fileName}");

                    MediaInfo? info = null;
                    if (_folderContext.ContainsKey(parentDir))
                    {
                        var cachedInfo = _folderContext[parentDir];
                        Log($"⚡ 命中目录缓存: {cachedInfo.title}");
                        var tempInfo = await CallAI(fileName);
                        if (tempInfo != null)
                        {
                            info = tempInfo;
                            info.title = cachedInfo.title;
                            info.original_title = cachedInfo.original_title;
                            info.year = cachedInfo.year;
                            info.type = cachedInfo.type;
                            info.tmdb_id = cachedInfo.tmdb_id;
                            if (cachedInfo.season == "0") info.season = "0";
                            CorrectEpisodeByRegex(fileName, info);
                            if (info.tmdb_id > 0) await GetEpisodeTitle("", info);
                        }
                    }
                    else
                    {
                        info = await CallAI(fileName);
                        if (info == null) throw new Exception("AI 识别返回空");
                        if (!string.IsNullOrEmpty(info.title)) info.title = Regex.Replace(info.title, @"\s*-\s*\d+$", "").Trim();
                        if (!string.IsNullOrEmpty(info.original_title)) info.original_title = Regex.Replace(info.original_title, @"\s*-\s*\d+$", "").Trim();
                        if (IsSpecialEpisode(info.original_title) || IsSpecialEpisode(fileName)) { Log("✨ 检测到特别篇/OVA，修正为 Season 0"); info.season = "0"; }
                        CorrectEpisodeByRegex(fileName, info);
                        Log($"🤖 AI 识别: {info.title} (S{info.season}E{info.episode})");
                        info = await CorrectByTmdb(info);
                        if (!string.IsNullOrEmpty(info.title)) { _folderContext[parentDir] = info; Log($"🔒 目录已锁定为: {info.title}"); }
                    }

                    if (info != null)
                    {
                        string finalPath = DoMoveOrLink(filePath, info);
                        if (!string.IsNullOrEmpty(finalPath)) { Log($"✅ 入库成功: {Path.GetFileName(finalPath)}"); break; }
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ 异常: {ex.Message} (将在 2秒后重试)", true);
                    await Task.Delay(2000);
                    if (i == maxRetries - 1) Log($"❌ 最终失败: {Path.GetFileName(filePath)}", true);
                }
                finally { dirLock.Release(); }
            }
        }

        private void CorrectEpisodeByRegex(string filename, MediaInfo info)
        {
            var match = Regex.Match(filename, @"\s-\s*(\d+)(?:\s|\[|\.)");
            if (match.Success)
            {
                string numStr = match.Groups[1].Value;
                if (info.episode != numStr)
                {
                    LogDebug($"[修正] AI 集数 '{info.episode}' -> 正则集数 '{numStr}'");
                    info.episode = numStr;
                }
            }
        }

        private async Task<MediaInfo?> CallAI(string filename)
        {
            try
            {
                string m = "", k = "", u = "";
                Dispatcher.Invoke(() => { m = TxtModel.Text; k = TxtApiKey.Password; u = TxtApiUrl.Text.TrimEnd('/'); });
                var p = $@"分析文件名 ""{filename}""。请直接返回一个标准的 JSON 对象。字段要求：1. ""title"": 中文译名 (如 '辉夜大小姐')。2. ""original_title"": 英文或罗马音原名。3. ""type"": ""Anime"" 或 ""Movie"" 或 ""TV""。4. ""year"": 年份。5. ""season"": 季号 (可以是数字或字符串 ""1"")。6. ""episode"": 集号 (可以是数字或字符串 ""01"")。";
                LogDebug($"[AI Prompt] {p.Replace("\n", " ")}");

                var req = new { model = m, messages = new[] { new { role = "user", content = p } } };
                var jsonRequest = JsonSerializer.Serialize(req);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, u + "/chat/completions");
                requestMessage.Headers.Add("Authorization", $"Bearer {k}");
                requestMessage.Content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

                var res = await _http.SendAsync(requestMessage);
                var str = await res.Content.ReadAsStringAsync();
                LogDebug($"[AI Response] {str}");

                if (!res.IsSuccessStatusCode) { Log($"❌ AI 请求失败: {res.StatusCode}", true); return null; }
                var content = JsonNode.Parse(str)?["choices"]?[0]?["message"]?["content"]?.ToString();
                if (content != null)
                {
                    content = content.Replace("```json", "").Replace("```", "").Trim();
                    int firstBrace = content.IndexOf('{'); int lastBrace = content.LastIndexOf('}');
                    if (firstBrace >= 0 && lastBrace > firstBrace) content = content.Substring(firstBrace, lastBrace - firstBrace + 1);
                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, NumberHandling = JsonNumberHandling.AllowReadingFromString, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
                    return JsonSerializer.Deserialize<MediaInfo>(content, options);
                }
                return null;
            }
            catch (Exception ex) { Log($"❌ AI 解析错误: {ex.Message}", true); return null; }
        }

        // 🔥 修复：使用 ShowDialog 替代 MessageBox
        private async void BtnTestAi_Click(object s, RoutedEventArgs e)
        {
            Log("🔄 正在测试 AI 连接...");
            try
            {
                string m = TxtModel.Text, k = TxtApiKey.Password, u = TxtApiUrl.Text.TrimEnd('/');
                if (string.IsNullOrWhiteSpace(k)) { await ShowDialog("错误", "请先填写 API Key"); return; }

                var req = new { model = m, messages = new[] { new { role = "user", content = "Say Hello" } } };
                var jsonRequest = JsonSerializer.Serialize(req);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, u + "/chat/completions");
                requestMessage.Headers.Add("Authorization", $"Bearer {k}");
                requestMessage.Content = new StringContent(jsonRequest, System.Text.Encoding.UTF8, "application/json");

                var res = await _http.SendAsync(requestMessage);
                if (res.IsSuccessStatusCode)
                {
                    Log("✅ API 连接成功！配置有效。");
                    await ShowDialog("成功", "✅ 连接成功！API 配置有效。");
                }
                else
                {
                    string err = await res.Content.ReadAsStringAsync();
                    Log($"❌ 连接失败: {res.StatusCode} - {err}", true);
                    await ShowDialog("失败", $"❌ 连接失败: {res.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Log($"❌ 测试异常: {ex.Message}", true);
                await ShowDialog("异常", $"❌ 发生异常: {ex.Message}");
            }
        }

        private async Task<MediaInfo> CorrectByTmdb(MediaInfo info)
        {
            string key = ""; Dispatcher.Invoke(() => key = TxtTmdbKey.Password.Trim());
            if (string.IsNullOrWhiteSpace(key)) return info;
            try
            {
                string type = info.type.ToLower() == "movie" ? "movie" : "tv";
                JsonNode? bestMatch = null;
                string searchTitle = CleanTitle(info.title);
                LogDebug($"[TMDB Search CN] {searchTitle}");
                bestMatch = await SearchTmdb(key, type, searchTitle);
                if (bestMatch == null && !string.IsNullOrEmpty(info.original_title))
                {
                    string cleanOriginal = CleanTitle(info.original_title);
                    LogDebug($"[TMDB Search Origin] {cleanOriginal}");
                    bestMatch = await SearchTmdb(key, type, cleanOriginal);
                }
                if (bestMatch != null)
                {
                    info.tmdb_id = bestMatch["id"]?.GetValue<int>() ?? 0;
                    info.title = type == "movie" ? bestMatch["title"]?.ToString() : bestMatch["name"]?.ToString();
                    string date = type == "movie" ? bestMatch["release_date"]?.ToString() : bestMatch["first_air_date"]?.ToString();
                    if (date?.Length >= 4 && int.TryParse(date.Substring(0, 4), out int y)) info.year = y;
                    Log($"✅ TMDB 锁定: {info.title} (ID: {info.tmdb_id})");
                    if (type == "tv" && info.tmdb_id > 0) await GetEpisodeTitle(key, info);
                }
            }
            catch (Exception ex) { Log($"⚠️ TMDB 错误: {ex.Message}", true); }
            return info;
        }

        private async Task<JsonNode?> SearchTmdb(string key, string type, string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            try
            {
                string url = $"https://api.themoviedb.org/3/search/{type}?api_key={key}&query={Uri.EscapeDataString(query)}&language=zh-CN";
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var res = await _http.GetStringAsync(url, cts.Token);
                var results = JsonNode.Parse(res)?["results"]?.AsArray();
                if (results != null && results.Count > 0) return results[0];
            }
            catch { }
            return null;
        }

        private async Task GetEpisodeTitle(string key, MediaInfo info)
        {
            try
            {
                if (string.IsNullOrEmpty(key)) Dispatcher.Invoke(() => key = TxtTmdbKey.Password.Trim());
                int seasonNum = info.GetSeasonInt() == 0 ? 1 : info.GetSeasonInt();
                string url = $"https://api.themoviedb.org/3/tv/{info.tmdb_id}/season/{seasonNum}?api_key={key}&language=zh-CN";
                LogDebug($"[TMDB Episode URL] {url}");
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var res = await _http.GetStringAsync(url, cts.Token);
                var episodes = JsonNode.Parse(res)?["episodes"]?.AsArray();
                if (episodes != null)
                {
                    foreach (var ep in episodes)
                    {
                        if (ep?["episode_number"]?.GetValue<int>() == info.GetEpisodeInt())
                        {
                            info.episode_title = ep?["name"]?.ToString();
                            Log($"📚 单集标题: {info.episode_title}");
                            break;
                        }
                    }
                }
            }
            catch { }
        }

        private string CleanTitle(string? input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            string clean = Regex.Replace(input, @"\[.*?\]|\(.*?\)|\{.*?\}", " ");
            clean = Regex.Replace(clean, @"(?i)(webrip|1080p|720p|hevc|x264|x265|aac|srtx2|10bit|assx2)", " ");
            return Regex.Replace(clean, @"\s+", " ").Trim();
        }

        private bool IsSpecialEpisode(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string[] keywords = { "OVA", "OAD", "Special", "SP", "Otona e no Kaidan", "Movie", "Gekijouban", "The Movie" };
            foreach (var kw in keywords) { if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) return true; }
            return false;
        }

        private string DoMoveOrLink(string src, MediaInfo info)
        {
            string root = ""; Dispatcher.Invoke(() => root = TxtTarget.Text);
            if (string.IsNullOrEmpty(root)) return "";
            string safeTitle = info.title?.Replace(":", " ") ?? "Unknown";
            foreach (var c in Path.GetInvalidFileNameChars()) safeTitle = safeTitle.Replace(c, '_');
            safeTitle = safeTitle.Trim();
            int sNum = info.GetSeasonInt(); int eNum = info.GetEpisodeInt();
            string cat = info.type == "Movie" ? "电影" : "动漫";
            string dir = info.type == "Movie" ? Path.Combine(root, cat, $"{safeTitle} ({info.year})") : Path.Combine(root, cat, safeTitle, $"Season {sNum}");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(src);
            string newName;
            if (info.type == "Movie") newName = $"{safeTitle} ({info.year}){ext}";
            else
            {
                string epTitle = string.IsNullOrWhiteSpace(info.episode_title) ? "" : $" - {info.episode_title}";
                foreach (var c in Path.GetInvalidFileNameChars()) epTitle = epTitle.Replace(c, '_');
                newName = $"{safeTitle} - S{sNum:D2}E{eNum:D2}{epTitle}{ext}";
            }
            string dest = Path.Combine(dir, newName);
            bool isLink = true; bool skip = true;
            Dispatcher.Invoke(() => { isLink = RadioLink.IsChecked == true; skip = SwSkipLink.IsChecked == true; });
            if (File.Exists(dest)) { if (skip) { Log($"⚠️ 跳过已存在: {newName}"); return dest; } else File.Delete(dest); }
            if (isLink) { if (CreateHardLink(dest, src, IntPtr.Zero)) { } else { Log("❌ 硬链失败，尝试移动...", true); isLink = false; } }
            if (!isLink) File.Move(src, dest);
            return dest;
        }

        private void BtnStart_Click(object s, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                if (_watcher != null) { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); _watcher = null; }
                Log("🛑 监控已停止"); _folderContext.Clear(); _dirLocks.Clear();
                BtnStart.Content = "启动全能监控"; BtnStart.Appearance = ControlAppearance.Primary;
            }
            else
            {
                if (!Directory.Exists(TxtSource.Text)) { ShowDialog("错误", "源目录不存在").ConfigureAwait(false); return; }
                SaveSettings();
                _watcher = new FileSystemWatcher(TxtSource.Text) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                _watcher.Created += async (s, ev) => await ProcessFile(ev.FullPath);
                _watcher.Renamed += async (s, ev) => await ProcessFile(ev.FullPath);
                Log($"🚀 监控启动中... [{TxtSource.Text}]");
                BtnStart.Content = "停止监控"; BtnStart.Appearance = ControlAppearance.Danger;
            }
            _isRunning = !_isRunning;
        }

        private async void BtnManual_Click(object s, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
            if (dlg.ShowDialog() == true) { foreach (var file in dlg.FileNames) await ProcessFile(file); Log("✅ 手动批处理完成"); }
        }

        // 🔥 修复：使用 ShowDialog 替代 MessageBox
        private async void BtnTestQb_Click(object s, RoutedEventArgs e)
        {
            bool ok = await Task.Run(async () => {
                try
                {
                    var url = TxtQbUrl.Text.TrimEnd('/');
                    var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("username", TxtQbUser.Text), new KeyValuePair<string, string>("password", TxtQbPass.Password) });
                    var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/api/v2/auth/login") { Content = content };
                    req.Headers.Referrer = new Uri(url);
                    var res = await _http.SendAsync(req);
                    return res.IsSuccessStatusCode && !(await res.Content.ReadAsStringAsync()).Contains("Fails.");
                }
                catch { return false; }
            });
            if (ok) await ShowDialog("成功", "✅ qBittorrent 连接成功"); else await ShowDialog("失败", "❌ 连接失败，请检查配置");
        }

        private void CmbProviders_SelectionChanged(object s, SelectionChangedEventArgs e)
        {
            if (!_isLoadingProfile && CmbProviders.SelectedItem is ApiProvider p) { TxtApiUrl.Text = p.Url; if (!string.IsNullOrEmpty(p.DefaultModel)) TxtModel.Text = p.DefaultModel; }
        }

        private void CmbProfiles_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (CmbProfiles.SelectedItem is AiProfile p) ApplyProfile(p); }

        private void ApplyProfile(AiProfile p)
        {
            _isLoadingProfile = true;
            if (p.ProviderIndex >= 0 && p.ProviderIndex < CmbProviders.Items.Count) CmbProviders.SelectedIndex = p.ProviderIndex;
            TxtRemark.Text = p.Remark; TxtApiUrl.Text = p.ApiUrl; TxtModel.Text = p.Model; TxtApiKey.Password = p.ApiKey; TxtTmdbKey.Password = p.TmdbKey;
            _isLoadingProfile = false;
        }

        // 🔥 修复：使用 ShowDialog 替代 MessageBox
        private async void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtRemark.Text)) { await ShowDialog("提示", "请填写配置备注名！"); return; }
            var newProfile = new AiProfile { Remark = TxtRemark.Text, ApiUrl = TxtApiUrl.Text, Model = TxtModel.Text, ApiKey = TxtApiKey.Password, TmdbKey = TxtTmdbKey.Password, ProviderIndex = CmbProviders.SelectedIndex };
            var existing = null as AiProfile;
            foreach (var p in Profiles) if (p.Remark == newProfile.Remark) { existing = p; break; }
            if (existing != null) Profiles.Remove(existing);
            Profiles.Add(newProfile);
            CmbProfiles.SelectedItem = newProfile;
            SaveSettings();

            await ShowDialog("保存成功", "配置已成功保存到本地预设。");
        }

        private void BtnDeleteProfile_Click(object sender, RoutedEventArgs e) { if (CmbProfiles.SelectedItem is AiProfile p) { Profiles.Remove(p); SaveSettings(); } }
        private void BtnSelectSource_Click(object s, RoutedEventArgs e) => TxtSource.Text = SelectFolder();
        private void BtnSelectTarget_Click(object s, RoutedEventArgs e) => TxtTarget.Text = SelectFolder();
        private string SelectFolder() { using var d = new WinForms.FolderBrowserDialog(); return d.ShowDialog() == WinForms.DialogResult.OK ? d.SelectedPath : ""; }
        private void BtnClearLog_Click(object s, RoutedEventArgs e) => TxtLog.Document.Blocks.Clear();

        private void Log(string msg, bool isError = false)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(msg, isError)); return; }
            var run = new Run($"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (isError) run.Foreground = MediaBrushes.OrangeRed;
            var paragraph = new Paragraph(run); paragraph.Margin = new Thickness(0);
            TxtLog.Document.Blocks.Add(paragraph); TxtLog.ScrollToEnd();
        }

        private void LogDebug(string msg) { bool isDebug = false; Dispatcher.Invoke(() => isDebug = ChkDebug.IsChecked == true); if (isDebug) Log($"[DEBUG] {msg}"); }

        private void LoadSettings()
        {
            try
            {
                if (File.Exists("settings.json"))
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText("settings.json"));
                    if (s != null)
                    {
                        TxtSource.Text = s.SourcePath; TxtTarget.Text = s.TargetPath;
                        RadioLink.IsChecked = s.IsHardLinkMode; RadioMove.IsChecked = !s.IsHardLinkMode;
                        TxtApiUrl.Text = s.AiUrl ?? ""; TxtApiKey.Password = s.AiKey ?? ""; TxtTmdbKey.Password = s.TmdbKey ?? "";
                        TxtModel.Text = s.AiModel ?? ""; TxtQbUrl.Text = s.QbUrl ?? ""; TxtQbUser.Text = s.QbUser ?? ""; TxtQbPass.Password = s.QbPass ?? "";
                        ChkDebug.IsChecked = s.IsDebugMode; SwAutoStart.IsChecked = s.AutoStart;
                        if (s.Profiles != null) foreach (var p in s.Profiles) Profiles.Add(p);
                        if (s.LastProfileIndex >= 0 && s.LastProfileIndex < Profiles.Count) CmbProfiles.SelectedIndex = s.LastProfileIndex;
                    }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                var s = new AppSettings
                {
                    SourcePath = TxtSource.Text,
                    TargetPath = TxtTarget.Text,
                    IsHardLinkMode = RadioLink.IsChecked == true,
                    AiUrl = TxtApiUrl.Text,
                    AiKey = TxtApiKey.Password,
                    TmdbKey = TxtTmdbKey.Password,
                    AiModel = TxtModel.Text,
                    QbUrl = TxtQbUrl.Text,
                    QbUser = TxtQbUser.Text,
                    QbPass = TxtQbPass.Password,
                    IsDebugMode = ChkDebug.IsChecked == true,
                    AutoStart = SwAutoStart.IsChecked == true,
                    Profiles = new List<AiProfile>(Profiles),
                    LastProfileIndex = CmbProfiles.SelectedIndex
                }; File.WriteAllText("settings.json", JsonSerializer.Serialize(s));
            }
            catch { }
        }
    }
}