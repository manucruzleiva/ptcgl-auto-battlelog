using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace PtcglAutoBattleLog.Tray.Logic
{
    /// <summary>DataContractJsonSerializer in and out of strings: it ships with .NET Framework, so no extra DLL.</summary>
    internal static class Json
    {
        /// <summary>Throws on malformed JSON (SerializationException and friends); callers decide what that means.</summary>
        internal static T Read<T>(string json) where T : class
        {
            // The serializer quietly turns "[]" or "1" into an empty object; every file here is an object.
            var trimmed = (json ?? "").TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{') throw new SerializationException("Expected a JSON object.");
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(FileOps.Utf8NoBom.GetBytes(json ?? "")))
                return (T)ser.ReadObject(ms);
        }

        internal static string Write<T>(T value)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    ser.WriteObject(w, value);
                    w.Flush();
                }
                return FileOps.Utf8NoBom.GetString(ms.ToArray());
            }
        }
    }
}
