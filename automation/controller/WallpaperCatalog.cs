using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace CodexDreamSkinController
{
    internal static class WallpaperCatalog
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        internal static string EngineRoot
        {
            get
            {
                try
                {
                    foreach (Process process in Process.GetProcessesByName("wallpaper64").Concat(Process.GetProcessesByName("wallpaper32")))
                    {
                        using (process)
                        {
                            try { string file = process.MainModule.FileName; if (File.Exists(file)) return Path.GetDirectoryName(file); }
                            catch { }
                        }
                    }
                }
                catch { }
                foreach (string steam in SteamLibraries())
                {
                    string root = Path.Combine(steam, "steamapps", "common", "wallpaper_engine");
                    if (File.Exists(Path.Combine(root, "wallpaper64.exe")) || File.Exists(Path.Combine(root, "wallpaper32.exe"))) return root;
                }
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (String.IsNullOrEmpty(programs)) programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(programs, "Steam", "steamapps", "common", "wallpaper_engine");
            }
        }

        internal static string EngineExecutable
        {
            get {
                string root = EngineRoot;
                string x64 = Path.Combine(root,"wallpaper64.exe"), x86 = Path.Combine(root,"wallpaper32.exe");
                foreach (Process process in Process.GetProcessesByName("wallpaper64").Concat(Process.GetProcessesByName("wallpaper32")))
                    using (process) try {
                        string image = process.MainModule.FileName;
                        if (!process.HasExited && (String.Equals(image,x64,StringComparison.OrdinalIgnoreCase) || String.Equals(image,x86,StringComparison.OrdinalIgnoreCase)) && WallpaperSafety.IsRegularLocalFile(image)) return image;
                    } catch { }
                return File.Exists(x64) ? x64 : x86;
            }
        }

        private static IEnumerable<string> SteamLibraries()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] registryKeys = { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" };
            foreach (string key in registryKeys)
            {
                foreach (string valueName in new [] { "SteamPath", "InstallPath" })
                {
                    try { string value = Convert.ToString(Registry.GetValue(key, valueName, null)); if (!String.IsNullOrWhiteSpace(value)) roots.Add(Path.GetFullPath(value)); }
                    catch { }
                }
            }
            foreach (Environment.SpecialFolder folder in new [] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            {
                string programs = Environment.GetFolderPath(folder);
                if (!String.IsNullOrEmpty(programs)) roots.Add(Path.Combine(programs, "Steam"));
            }
            foreach (string steam in roots.ToArray())
            {
                string libraries = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraries)) continue;
                try
                {
                    foreach (Match match in Regex.Matches(File.ReadAllText(libraries, Encoding.UTF8), "\"path\"\\s+\"([^\"]+)\""))
                    {
                        try { roots.Add(Path.GetFullPath(match.Groups[1].Value.Replace("\\\\", "\\"))); }
                        catch { }
                    }
                }
                catch { }
            }
            return roots;
        }

        private static IEnumerable<string> ProjectRoots()
        {
            string engine = EngineRoot;
            string steamApps = Directory.GetParent(Directory.GetParent(engine).FullName).FullName;
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            roots.Add(Path.Combine(steamApps, "workshop", "content", "431960"));
            roots.Add(Path.Combine(engine, "projects", "myprojects"));
            roots.Add(Path.Combine(engine, "projects", "defaultprojects"));
            foreach (string library in SteamLibraries()) roots.Add(Path.Combine(library, "steamapps", "workshop", "content", "431960"));
            string libraries = Path.Combine(steamApps, "libraryfolders.vdf");
            if (File.Exists(libraries))
            {
                foreach (Match match in Regex.Matches(File.ReadAllText(libraries, Encoding.UTF8), "\"path\"\\s+\"([^\"]+)\""))
                {
                    try {
                        string library = Path.GetFullPath(match.Groups[1].Value.Replace("\\\\", "\\"));
                        string workshop = Path.Combine(library, "steamapps", "workshop", "content", "431960");
                        if (Directory.Exists(workshop)) roots.Add(workshop);
                    }
                    catch { }
                }
            }
            return roots;
        }

        internal static bool IsAllowedSource(string candidate)
        {
            if (!WallpaperSafety.IsRegularLocalFile(candidate)) return false;
            string full = Path.GetFullPath(candidate);
            foreach (string root in ProjectRoots())
            {
                if (!Directory.Exists(root)) continue;
                string resolved = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (full.StartsWith(resolved, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static List<ThemeItem> Discover() { return Discover(ProjectRoots()); }

        internal static List<ThemeItem> Discover(IEnumerable<string> projectRoots)
        {
            List<ThemeItem> result = new List<ThemeItem>();
            foreach (string root in projectRoots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (string directory in Directory.GetDirectories(root))
                {
                    try
                    {
                        string projectFile = Path.Combine(directory, "project.json");
                        if (!File.Exists(projectFile)) continue;
                        Dictionary<string, object> project = WallpaperSafety.ReadProject(projectFile);
                        if (project == null) continue;
                        string declaredFile = StringValue(project, "file");
                        string type = StringValue(project, "type").ToLowerInvariant();
                        if (type.Length == 0)
                        {
                            string declaredExtension = Path.GetExtension(declaredFile).ToLowerInvariant();
                            if (project.ContainsKey("dependency") && project.ContainsKey("preset")) type = "preset";
                            else if (declaredExtension == ".json" || declaredExtension == ".pkg") type = "scene";
                            else if (declaredExtension == ".exe") type = "application";
                            else if (declaredExtension == ".html" || declaredExtension == ".htm") type = "web";
                            else if (declaredExtension == ".mp4" || declaredExtension == ".webm" || declaredExtension == ".m4v") type = "video";
                        }
                        string unavailable = null;
                        if (type == "preset") unavailable = "这是预设，缺少原壁纸资源（" + StringValue(project, "dependency") + "）。";
                        else if (type == "application") unavailable = "应用壁纸使用独立程序，目前不能嵌入 Codex 背景。";
                        else if (type != "video" && type != "scene" && type != "web") unavailable = "此壁纸类型暂不能作为 Codex 背景。";
                        string mediaPath = type == "video" ? Path.GetFullPath(Path.Combine(directory, declaredFile)) : projectFile;
                        if (!IsWithin(mediaPath, directory) || !WallpaperSafety.IsRegularLocalFile(mediaPath)) continue;
                        string extension = Path.GetExtension(mediaPath).ToLowerInvariant();
                        if (type == "video" && extension != ".mp4" && extension != ".webm" && extension != ".m4v") unavailable = "此视频格式暂不能播放。";
                        if (type == "scene" || type == "web")
                        {
                            string resource = declaredFile.Length > 0 ? Path.GetFullPath(Path.Combine(directory, declaredFile)) : Path.Combine(directory, "scene.pkg");
                            bool packagedScene = type == "scene" && WallpaperSafety.IsRegularLocalFile(Path.Combine(directory, "scene.pkg"));
                            if (!IsWithin(resource, directory) || (!WallpaperSafety.IsRegularLocalFile(resource) && !packagedScene)) unavailable = "壁纸资源不完整，请先在 Wallpaper Engine 中下载完成。";
                        }
                        string preview = null;
                        foreach (string name in new string[] { StringValue(project, "preview"), "preview.jpg", "preview.jpeg", "preview.png", "preview.gif" })
                        {
                            if (String.IsNullOrWhiteSpace(name)) continue;
                            string path = Path.Combine(directory, name);
                            if (IsWithin(path, directory) && WallpaperSafety.IsRegularLocalFile(path) && new FileInfo(path).Length <= 16777216) { preview = path; break; }
                        }
                        string nameText = StringValue(project, "title").Replace('\r', ' ').Replace('\n', ' ').Trim();
                        if (nameText.Length == 0) nameText = Path.GetFileName(directory);
                        if (nameText.Length > 100) nameText = nameText.Substring(0, 100);
                        string id = "we-" + StableId(projectFile);
                        result.Add(new ThemeItem {
                            Id = id, Name = nameText, Directory = directory, Source = "wallpaper",
                            MediaKind = type, MediaPath = mediaPath, PreviewPath = preview, UnavailableReason = unavailable
                        });
                    }
                    catch { }
                }
            }
            foreach (ThemeItem preset in result.Where(item => item.MediaKind == "preset").ToList())
            {
                try
                {
                    var metadata = WallpaperSafety.ReadProject(Path.Combine(preset.Directory, "project.json"));
                    string dependency = StringValue(metadata, "dependency");
                    ThemeItem original = result.FirstOrDefault(item => Path.GetFileName(item.Directory) == dependency &&
                        String.IsNullOrEmpty(item.UnavailableReason) && (item.MediaKind == "video" || item.MediaKind == "scene" || item.MediaKind == "web"));
                    object propertiesValue;
                    var properties = metadata.TryGetValue("preset", out propertiesValue) ? propertiesValue as Dictionary<string, object> : null;
                    if (original == null || properties == null) continue;
                    preset.MediaKind = original.MediaKind;
                    preset.MediaPath = original.MediaPath;
                    preset.IsPreset = true;
                    preset.MotionProperties = new Dictionary<string, object>();
                    var originalMetadata = WallpaperSafety.ReadProject(Path.Combine(original.Directory, "project.json"));
                    var definitions = ObjectValue(ObjectValue(originalMetadata, "general"), "properties");
                    foreach (var property in properties)
                    {
                        object value = property.Value;
                        if (value == null || (definitions != null && !definitions.ContainsKey(property.Key))) continue;
                        var definition = definitions == null ? null : ObjectValue(definitions, property.Key);
                        if (definition != null && StringValue(definition, "type") == "text") continue;
                        string text = value as string;
                        if (!String.IsNullOrWhiteSpace(text))
                        {
                            try
                            {
                                string asset = Path.GetFullPath(Path.Combine(preset.Directory, text));
                                if (IsWithin(asset, preset.Directory) && File.Exists(asset)) value = asset.Replace('\\', '/');
                            }
                            catch { }
                        }
                        preset.MotionProperties[property.Key] = value;
                    }
                    preset.UnavailableReason = null;
                }
                catch { }
            }
            return result;
        }

        internal static ThemeItem Current()
        {
            string config = Path.Combine(EngineRoot, "config.json");
            if (!File.Exists(config)) throw new InvalidOperationException("找不到 Wallpaper Engine 的当前壁纸配置。");
            return CurrentFromConfig(File.ReadAllText(config, Encoding.UTF8), Environment.UserName, Discover());
        }

        internal static ThemeItem CurrentFromConfig(string text, string user, IEnumerable<ThemeItem> catalog)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 8388608 }.DeserializeObject(text) as Dictionary<string, object>;
            if (root == null) return null;
            object profileValue;
            var profile = root.TryGetValue(user, out profileValue) ? profileValue as Dictionary<string, object> : null;
            var general = ObjectValue(profile, "general");
            var config = ObjectValue(general, "wallpaperconfig");
            var monitors = ObjectValue(config, "selectedwallpapers");
            if (monitors == null) return null;
            var selected = ObjectValue(monitors, "Monitor0") ?? monitors.Values.OfType<Dictionary<string, object>>().FirstOrDefault();
            if (selected == null) return null;
            string file = StringValue(selected, "file");
            if (String.IsNullOrWhiteSpace(file)) return null;
            string active = Path.GetFullPath(file);
            string directory = Path.GetDirectoryName(active);
            return catalog.FirstOrDefault(item => String.Equals(active, item.MediaPath, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(directory, Path.GetFullPath(item.Directory), StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> ObjectValue(Dictionary<string, object> value, string key)
        {
            object nested;
            return value != null && value.TryGetValue(key, out nested) ? nested as Dictionary<string, object> : null;
        }

        internal static string Import(ThemeItem item, string savedThemesRoot)
        {
            ThemeItem canonical = Discover().FirstOrDefault(candidate =>
                String.Equals(candidate.Id, item.Id, StringComparison.Ordinal) &&
                String.Equals(candidate.MediaPath, item.MediaPath, StringComparison.OrdinalIgnoreCase));
            if (canonical == null) throw new InvalidOperationException("Wallpaper Engine 壁纸已移动或不可用，请刷新列表。");
            if (!String.IsNullOrEmpty(canonical.UnavailableReason)) throw new InvalidOperationException(canonical.UnavailableReason);
            if (!IsAllowedSource(canonical.MediaPath)) throw new InvalidOperationException("壁纸路径未通过安全检查。");
            var safeProperties = WallpaperSafety.NormalizeProperties(canonical.MediaPath, canonical.MotionProperties);
            WallpaperSafety.PropertyBatches(MotionHost.PropertiesJson(safeProperties));
            string target = Path.Combine(savedThemesRoot, "wallpaper-" + canonical.Id);
            Directory.CreateDirectory(target);
            string poster = Path.Combine(target, "poster.png");
            string temporaryPoster = poster + ".tmp";
            if (!String.IsNullOrEmpty(canonical.PreviewPath))
            {
                using (MemoryStream memory = new MemoryStream(File.ReadAllBytes(canonical.PreviewPath)))
                using (Image image = Image.FromStream(memory))
                using (Bitmap bitmap = new Bitmap(image))
                    bitmap.Save(temporaryPoster, ImageFormat.Png);
            }
            else
            {
                using (Bitmap bitmap = new Bitmap(640, 360))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.FromArgb(19, 33, 54));
                    bitmap.Save(temporaryPoster, ImageFormat.Png);
                }
            }
            if (File.Exists(poster)) File.Delete(poster);
            File.Move(temporaryPoster, poster);

            Dictionary<string, object> theme = new Dictionary<string, object>();
            theme["schemaVersion"] = 1;
            theme["id"] = canonical.Id;
            theme["name"] = canonical.Name;
            theme["image"] = "poster.png";
            theme["appearance"] = "auto";
            theme["art"] = new Dictionary<string, object> {
                { "focusX", 0.5 }, { "focusY", 0.5 }, { "safeArea", "left" }, { "taskMode", "ambient" }
            };
            theme["motion"] = new Dictionary<string, object> {
                { "kind", canonical.MediaKind }, { "source", canonical.MediaPath }
            };
            if (safeProperties.Count > 0)
                ((Dictionary<string, object>)theme["motion"])["properties"] = safeProperties;
            string control = Path.Combine(Directory.GetParent(savedThemesRoot).FullName, "control");
            Directory.CreateDirectory(control);
            SaveRoots(control);
            string tokenPath = Path.Combine(control, "motion-token.txt");
            if (!File.Exists(tokenPath))
            {
                byte[] bytes = new byte[32];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
                File.WriteAllText(tokenPath, BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(), new UTF8Encoding(false));
            }
            string themePath = Path.Combine(target, "theme.json");
            string temporaryTheme = themePath + ".tmp";
            File.WriteAllText(temporaryTheme, Json.Serialize(theme) + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(themePath)) File.Delete(themePath);
            File.Move(temporaryTheme, themePath);
            return target;
        }

        internal static void SaveRoots(string control)
        {
            string path = Path.Combine(control, "wallpaper-roots.json");
            string content = new JavaScriptSerializer().Serialize(ProjectRoots().Select(Path.GetFullPath).ToArray());
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content) return;
            Directory.CreateDirectory(control);
            string temporary = path + "." + Process.GetCurrentProcess().Id + ".tmp";
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        private static string StringValue(Dictionary<string, object> value, string key)
        {
            object item;
            return value.TryGetValue(key, out item) && item != null ? Convert.ToString(item) : "";
        }

        private static string StableId(string text)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToLowerInvariant()));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }

        private static bool IsWithin(string candidate, string root)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(candidate).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
