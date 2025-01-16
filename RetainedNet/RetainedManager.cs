using HarmonyLib;
using System.Collections.Generic;
using System.IO.Compression;
using System.IO;
using System.Reflection.Emit;
using System.Reflection;
using System.Text;
using System;
using System.Text.Json;

namespace RetainedNet
{
    public class RetainedManager
    {
        private static readonly JsonSerializerOptions _serializerOptions = new JsonSerializerOptions { IncludeFields = true };
        private static readonly Harmony _harmony = new Harmony(nameof(RetainedManager));

        private static string _propertyNamespace;
        private static Type _propertyReturnType;

        private static Dictionary<string, string> _backingDictionary = new Dictionary<string, string>();
        public static Func<byte[]> OnLoad;
        public static Action<byte[]> OnSave;

        public static void PatchAll()
        {
            if (OnLoad == null && File.Exists("retained.bin"))
                _backingDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(Decompress(File.ReadAllBytes("retained.bin")));
            else if (OnLoad != null)
                _backingDictionary = JsonSerializer.Deserialize<Dictionary<string, string>>(Decompress(OnLoad.Invoke()));

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                Patch(assembly);
        }

        public static void Patch(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (property.GetCustomAttribute<RetainedAttribute>() == null)
                        continue;
                    var sb = new StringBuilder();
                    sb.Append(type.Namespace);
                    sb.Append(".");
                    sb.Append(type.Name);
                    sb.Append("::");
                    sb.Append(property.Name);

                    _propertyNamespace = sb.ToString();
                    _propertyReturnType = property.PropertyType;

                    if (_backingDictionary.ContainsKey(_propertyNamespace))
                        property.SetValue(null, JsonSerializer.Deserialize(_backingDictionary[_propertyNamespace], _propertyReturnType, _serializerOptions));

                    _harmony.Patch(property.SetMethod, transpiler: AccessTools.Method(typeof(RetainedManager), nameof(SetterTranspiler)));
                }
            }
        }

        private static IEnumerable<CodeInstruction> SetterTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldstr, _propertyNamespace);
            yield return CodeInstruction.LoadArgument(0);
            if (_propertyReturnType.IsValueType)
                yield return new CodeInstruction(OpCodes.Box, _propertyReturnType);
            yield return CodeInstruction.Call(typeof(RetainedManager), nameof(SetterInterceptor));

            foreach (var instruction in instructions)
                yield return instruction;
        }

        private static void SetterInterceptor(string propertyNamespace, object data)
        {
            if (_backingDictionary.ContainsKey(propertyNamespace))
                _backingDictionary[propertyNamespace] = JsonSerializer.Serialize(data, _serializerOptions);
            else _backingDictionary.Add(propertyNamespace, JsonSerializer.Serialize(data, _serializerOptions));

            using (var ms = new MemoryStream())
            {
                JsonSerializer.Serialize(ms, _backingDictionary, _serializerOptions);

                var compressedBackingDict = Compress(ms.ToArray());

                if (OnSave == null)
                    File.WriteAllBytes("retained.bin", compressedBackingDict);
                else OnSave.Invoke(compressedBackingDict);
            }
        }

        private static byte[] Decompress(byte[] input)
        {
            using (var outMs = new MemoryStream())
            {
                using (var inMs = new MemoryStream(input))
                {
                    using (var decompressor = new DeflateStream(inMs, CompressionMode.Decompress))
                    {
                        decompressor.CopyTo(outMs);
                    }
                }

                return outMs.ToArray();
            }
        }

        private static byte[] Compress(byte[] input)
        {
            using (var outMs = new MemoryStream())
            {
                using (var compressor = new DeflateStream(outMs, CompressionLevel.Fastest))
                {
                    compressor.Write(input, 0, input.Length);
                }

                return outMs.ToArray();
            }
        }
    }
}