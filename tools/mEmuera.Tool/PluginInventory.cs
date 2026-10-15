using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

internal static class PluginInventory
{
    public static async Task<int> RunAsync(string root, string output)
    {
        var libraries = new List<object>();
        foreach (string path in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories)
            .Where(p => Path.GetRelativePath(root, p).Contains("plugin", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var metadata = pe.GetMetadataReader(); var bindings = new List<object>();
            foreach (var handle in metadata.TypeDefinitions)
            {
                var type = metadata.GetTypeDefinition(handle);
                foreach (var methodHandle in type.GetMethods())
                {
                    var method = metadata.GetMethodDefinition(methodHandle);
                    if (metadata.GetString(method.Name) != "get_Name" || method.RelativeVirtualAddress == 0) continue;
                    var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                    if (il is null) continue;
                    // 只认直接返回常量的 getter；其他 IL 保留 unknown，不执行插件。
                    int start = 0; while (start < il.Length && il[start] == 0) start++;
                    if (start + 6 != il.Length || il[start] != 0x72 || il[^1] != 0x2a) continue;
                    int token = BitConverter.ToInt32(il, start + 1);
                    string name = metadata.GetUserString(MetadataTokens.UserStringHandle(token & 0xFFFFFF));
                    if (name is not ("Chat" or "ModelSet")) continue;
                    bindings.Add(new { method = name, type = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name),
                        evidence = "metadata get_Name: ldstr/ret", executed = false });
                }
            }
            stream.Position = 0;
            libraries.Add(new { path = Path.GetRelativePath(root, path).Replace('\\', '/'), sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream)),
                references = metadata.AssemblyReferences.Select(h => { var reference = metadata.GetAssemblyReference(h); return new { name = metadata.GetString(reference.Name), version = reference.Version.ToString() }; }).ToArray(), bindings });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { libraries, executed = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"pluginLibraries={libraries.Count}"); return 0;
    }
}
