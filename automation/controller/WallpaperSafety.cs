using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace CodexDreamSkinController
{
    // Wallpaper metadata is untrusted input. Validate before changing the active
    // theme and again before handing any path or value to the native CLI.
    internal static class WallpaperSafety
    {
        internal static string BlockedMessage(string stateRoot, string sourcePath)
        {
            string recordPath = Path.Combine(stateRoot,"control","motion-safety.json");
            try {
                foreach (var failure in ReadFailures(recordPath)) {
                    string failedSource = Convert.ToString(failure["source"]);
                    if (String.IsNullOrEmpty(failedSource) || String.Equals(failedSource,sourcePath,StringComparison.OrdinalIgnoreCase))
                        return Convert.ToString(failure["message"]);
                }
                return null;
            } catch { return "壁纸安全状态暂不可验证，已停止自动重试。"; }
        }

        private static List<Dictionary<string,object>> ReadFailures(string path)
        {
            var failures = new List<Dictionary<string,object>>();
            if (!File.Exists(path)) return failures;
            if (!IsRegularLocalFile(path) || new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid safety record");
            var record = new JavaScriptSerializer {MaxJsonLength=65536,RecursionLimit=8}.DeserializeObject(File.ReadAllText(path,Encoding.UTF8)) as Dictionary<string,object>;
            object blocked, entries;
            if (record == null || !record.TryGetValue("blocked",out blocked) || !(blocked is bool)) throw new InvalidDataException("Invalid safety record");
            if (!(bool)blocked) return failures;
            if (record.TryGetValue("failures",out entries)) {
                var array = entries as object[];
                if (array == null || array.Length == 0 || array.Length > 64) throw new InvalidDataException("Invalid failure list");
                foreach (object entry in array) {
                    var failure = entry as Dictionary<string,object>;
                    if (failure == null) throw new InvalidDataException("Invalid failure entry");
                    failures.Add(failure);
                }
            } else failures.Add(record); // Read legacy single-source records.
            foreach (var failure in failures) {
                object source, message;
                if (!failure.TryGetValue("source",out source) || !(source is string) ||
                    !failure.TryGetValue("message",out message) || !(message is string) || String.IsNullOrWhiteSpace((string)message) ||
                    ((string)source).Length > 2048 || ((string)message).Length > 1024)
                    throw new InvalidDataException("Invalid failure entry");
            }
            return failures;
        }

        internal static void RecordFailure(string stateRoot, string sourcePath, string code, string message)
        {
            sourcePath=sourcePath ?? "";
            if (sourcePath.Length>2048 || sourcePath.Any(Char.IsControl)) sourcePath="";
            code=String.IsNullOrEmpty(code) || !Regex.IsMatch(code,"^[a-z-]{1,64}$") ? "native-failure" : code;
            message=String.IsNullOrWhiteSpace(message) ? "壁纸异常，已停止自动重试。" : message;
            message=message.Replace('\r',' ').Replace('\n',' ');
            if (message.Length>1024) message=message.Substring(0,1024);
            string pausePath = Path.Combine(stateRoot,"paused");
            // Either durable marker protects a replacement host; the in-memory
            // latch remains set even if the state directory becomes unwritable.
            try { WriteAtomic(pausePath,"paused by wallpaper safety guard"); } catch { }
            string recordPath = Path.Combine(stateRoot,"control","motion-safety.json");
            List<Dictionary<string,object>> failures;
            try { failures = ReadFailures(recordPath); }
            catch { failures = new List<Dictionary<string,object>> {new Dictionary<string,object>{{"source",""},{"message","安全记录无法验证，请检查后再启用壁纸。"}}}; }
            failures.RemoveAll(entry => String.Equals(Convert.ToString(entry["source"]),sourcePath ?? "",StringComparison.OrdinalIgnoreCase));
            if (failures.Count >= 64) { failures = failures.Take(62).ToList(); failures.Add(new Dictionary<string,object>{{"source",""},{"message","壁纸故障数量达到安全上限，请检查后再启用。"}}); }
            failures.Add(new Dictionary<string,object>{{"source",sourcePath ?? ""},{"code",code},{"message",message},{"time",DateTimeOffset.Now.ToString("o")}});
            WriteAtomic(recordPath,new JavaScriptSerializer().Serialize(new Dictionary<string,object>{{"blocked",true},{"failures",failures}}));
            if (!File.Exists(pausePath)) WriteAtomic(pausePath,"paused by wallpaper safety guard");
        }

        private static void WriteAtomic(string path, string value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try {
                File.WriteAllText(temporary,value,new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary,path,null); else File.Move(temporary,path);
            } finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        internal static bool IsRegularLocalFile(string path)
        {
            try {
                if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || path.StartsWith(@"\\") ||
                    path.IndexOfAny(new []{'\0','\r','\n'}) >= 0 || !File.Exists(path)) return false;
                string full = Path.GetFullPath(path);
                if (full.Substring(Path.GetPathRoot(full).Length).Contains(":")) return false;
                for (FileSystemInfo item = new FileInfo(full); item != null; item = item is FileInfo ?
                    (FileSystemInfo)((FileInfo)item).Directory : ((DirectoryInfo)item).Parent)
                    if ((item.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                return true;
            } catch { return false; }
        }

        internal static Dictionary<string, object> ReadProject(string path)
        {
            if (!IsRegularLocalFile(path) || new FileInfo(path).Length > 1048576)
                throw new InvalidOperationException("壁纸配置不是安全的本地文件，或超过大小限制。");
            var json = new JavaScriptSerializer { MaxJsonLength = 1048576, RecursionLimit = 32 };
            var result = json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
            if (result == null) throw new InvalidOperationException("壁纸配置格式无效。");
            return result;
        }

        internal static Dictionary<string, object> ObjectValue(Dictionary<string, object> value, string key)
        {
            object child;
            return value != null && value.TryGetValue(key, out child) ? child as Dictionary<string, object> : null;
        }

        internal static Dictionary<string, object> NormalizeProperties(string sourcePath, Dictionary<string, object> requested)
        {
            var result = new Dictionary<string, object>();
            if (requested == null) return result;
            if (requested.Count > 256) throw new InvalidOperationException("壁纸属性数量超过安全限制。");
            var definitions = Path.GetFileName(sourcePath).Equals("project.json", StringComparison.OrdinalIgnoreCase)
                ? ObjectValue(ObjectValue(ReadProject(sourcePath), "general"), "properties") : null;
            foreach (var entry in requested) {
                if (!Regex.IsMatch(entry.Key, "^[A-Za-z0-9_]{1,128}$")) throw new InvalidOperationException("壁纸属性名称无效。");
                if (entry.Key == "volume" || entry.Value == null) continue;
                var definition = ObjectValue(definitions, entry.Key);
                if (definitions == null || definition == null) continue;
                object typeValue; string type = definition.TryGetValue("type", out typeValue) ? Convert.ToString(typeValue) : "";
                object value = entry.Value;
                string stringValue = value as string;
                if (stringValue != null && (stringValue.Length > 1024 || stringValue.Any(Char.IsControl)))
                    throw new InvalidOperationException("壁纸属性文字或资源路径超过安全限制："+entry.Key);
                if (type == "text") continue; // Editor labels, not writable properties.
                if (type == "bool") {
                    if (!(value is bool)) throw new InvalidOperationException("壁纸开关属性类型无效：" + entry.Key);
                } else if (type == "slider") {
                    double number = Number(value, entry.Key);
                    object limit;
                    if (definition.TryGetValue("min", out limit) && number < Number(limit, entry.Key) - 0.000001 ||
                        definition.TryGetValue("max", out limit) && number > Number(limit, entry.Key) + 0.000001)
                        throw new InvalidOperationException("壁纸滑块属性超出范围：" + entry.Key);
                } else if (type == "combo") {
                    object optionsValue; var options = definition.TryGetValue("options", out optionsValue) ? optionsValue as object[] : null;
                    if (options == null || !options.OfType<Dictionary<string, object>>().Any(option =>
                        option.ContainsKey("value") && Convert.ToString(option["value"], CultureInfo.InvariantCulture) == Convert.ToString(value, CultureInfo.InvariantCulture)))
                        throw new InvalidOperationException("壁纸选项不在允许列表：" + entry.Key);
                    value = Convert.ToString(value, CultureInfo.InvariantCulture);
                } else if (type == "color") {
                    string color = value as string; double component;
                    string[] channels = color == null ? new string[0] : color.Split(new []{' '}, StringSplitOptions.RemoveEmptyEntries);
                    if ((channels.Length != 3 && channels.Length != 4) || channels.Any(channel =>
                        !Double.TryParse(channel, NumberStyles.Float, CultureInfo.InvariantCulture, out component) ||
                        Double.IsNaN(component) || Double.IsInfinity(component) || component < 0 || component > 1))
                        throw new InvalidOperationException("壁纸颜色属性无效：" + entry.Key);
                } else if (type == "textinput") {
                    string text = value as string;
                    if (text == null || text.Length > 512 || text.Any(Char.IsControl))
                        throw new InvalidOperationException("壁纸文字属性超过安全限制：" + entry.Key);
                } else if (type == "scenetexture" || type == "file") {
                    string file = value as string;
                    if (String.IsNullOrEmpty(file)) { value = ""; }
                    else if (!IsRegularLocalFile(file) || !WallpaperCatalog.IsAllowedSource(file))
                        throw new InvalidOperationException("壁纸附加资源不在本地壁纸库或已经缺失：" + entry.Key);
                } else continue;
                result[entry.Key] = value;
            }
            return result;
        }

        private static double Number(object value, string key)
        {
            if (!(value is byte || value is short || value is int || value is long || value is float || value is double || value is decimal))
                throw new InvalidOperationException("壁纸数值属性类型无效：" + key);
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            if (Double.IsNaN(number) || Double.IsInfinity(number) || Math.Abs(number) > 100000000)
                throw new InvalidOperationException("壁纸数值属性超过安全限制：" + key);
            return number;
        }

        internal static string QuoteArgument(string value)
        {
            if (value == null || value.IndexOfAny(new []{'\0','\r','\n'}) >= 0)
                throw new InvalidOperationException("壁纸命令参数无效。");
            var text = new StringBuilder("\""); int slashes = 0;
            foreach (char character in value) {
                if (character == '\\') { slashes++; continue; }
                text.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                text.Append(character); slashes = 0;
            }
            text.Append('\\', slashes * 2).Append('"');
            return text.ToString();
        }

        internal static List<string> PropertyBatches(string json)
        {
            var input = new JavaScriptSerializer { MaxJsonLength = 20000, RecursionLimit = 8 }.DeserializeObject(json) as Dictionary<string, object>;
            if (input == null || input.Count > 256) throw new InvalidOperationException("壁纸属性格式无效。");
            var batches = new List<string>();
            var current = new SortedDictionary<string, object>(StringComparer.Ordinal) {{"volume",0}};
            var serializer = new JavaScriptSerializer();
            foreach (var item in input.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
                if (item.Key == "volume") continue;
                if (!Regex.IsMatch(item.Key, "^[A-Za-z0-9_]{1,128}$") || item.Value == null || item.Value is Dictionary<string, object> || item.Value is object[])
                    throw new InvalidOperationException("壁纸属性包含不允许的数据。");
                string value = item.Value as string;
                if (value != null) {
                    if (value.Length > 1024 || value.Any(Char.IsControl)) throw new InvalidOperationException("壁纸属性文字超过安全限制。");
                } else if (!(item.Value is bool)) Number(item.Value,item.Key);
                current[item.Key] = item.Value;
                string payload = serializer.Serialize(current).Replace(")~END", "\\u0029~END");
                if (Encoding.UTF8.GetByteCount(payload) <= 1536) continue;
                current.Remove(item.Key);
                batches.Add(serializer.Serialize(current).Replace(")~END", "\\u0029~END"));
                current = new SortedDictionary<string, object>(StringComparer.Ordinal) {{"volume",0},{item.Key,item.Value}};
                if (Encoding.UTF8.GetByteCount(serializer.Serialize(current)) > 1536) throw new InvalidOperationException("单项壁纸属性过长。");
            }
            batches.Add(serializer.Serialize(current).Replace(")~END", "\\u0029~END"));
            if (batches.Count > 16) throw new InvalidOperationException("壁纸设置批次数量超过限制。");
            return batches;
        }
    }
}
