
using System;
using System.IO;
using System.Reflection;
using System.Collections.Generic;

class ILScanner {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type saType = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        FieldInfo f_gTb = saType.GetField("gTbYFnJa7EN", BindingFlags.Static | BindingFlags.NonPublic);
        f_gTb.SetValue(null, 100);
        MethodInfo strMethod = saType.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        int saToken = strMethod.MetadataToken;
        Console.WriteLine("SA String Method Token: " + saToken.ToString("X8"));

        HashSet<int> allOffsets = new HashSet<int>();
        HashSet<int> uiPropertyOffsets = new HashSet<int>();
        HashSet<int> unsafeOffsets = new HashSet<int>(); // set_Name, XML, SQL, Resource, etc.
        Dictionary<string, int> consumerCounts = new Dictionary<string, int>();

        Type[] types;
        try {
            types = asm.GetTypes();
        } catch (ReflectionTypeLoadException ex) {
            types = ex.Types;
        }

        int methodCount = 0;
        foreach (Type t in types) {
            if (t == null) continue;
            MethodBase[] methods = FindAllMethods(t);
            foreach (MethodBase mb in methods) {
                MethodBody body = null;
                try { body = mb.GetMethodBody(); } catch { continue; }
                if (body == null) continue;
                methodCount++;
                byte[] il = body.GetILAsByteArray();
                ScanIL(il, mod, saToken, allOffsets, uiPropertyOffsets, unsafeOffsets, consumerCounts);
            }
        }

        Console.WriteLine("Scanned methods: " + methodCount);
        Console.WriteLine("Total unique SA string offsets referenced in IL: " + allOffsets.Count);
        Console.WriteLine("Offsets directly passed to UI properties/methods: " + uiPropertyOffsets.Count);
        Console.WriteLine("Offsets passed to unsafe/internal APIs: " + unsafeOffsets.Count);
        
        int pureSafeUI = 0;
        foreach (int off in uiPropertyOffsets) {
            if (!unsafeOffsets.Contains(off)) pureSafeUI++;
        }
        Console.WriteLine("Pure safe UI offsets (never used in unsafe APIs): " + pureSafeUI);
    }

    static MethodBase[] FindAllMethods(Type t) {
        List<MethodBase> list = new List<MethodBase>();
        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        try { list.AddRange(t.GetMethods(flags)); } catch {}
        try { list.AddRange(t.GetConstructors(flags)); } catch {}
        return list.ToArray();
    }

    static void ScanIL(byte[] il, Module mod, int saToken, HashSet<int> allOffsets, HashSet<int> uiOffsets, HashSet<int> unsafeOffsets, Dictionary<string, int> consumerCounts) {
        for (int i = 0; i < il.Length - 10; i++) {
            // Look for ldc.i4 (0x20) followed by call saToken (0x28)
            if (il[i] == 0x20 && il[i + 5] == 0x28) {
                int callToken = BitConverter.ToInt32(il, i + 6);
                if (callToken == saToken) {
                    int offset = BitConverter.ToInt32(il, i + 1);
                    allOffsets.Add(offset);
                    
                    // Check what happens right after i + 10
                    int nextPos = i + 10;
                    if (nextPos < il.Length - 5 && (il[nextPos] == 0x28 || il[nextPos] == 0x6F || il[nextPos] == 0x73)) {
                        int nextToken = BitConverter.ToInt32(il, nextPos + 1);
                        try {
                            MemberInfo nextMem = mod.ResolveMember(nextToken);
                            string sig = (nextMem.DeclaringType != null ? nextMem.DeclaringType.Name : "") + "." + nextMem.Name;
                            if (IsUIConsumer( nextMem.Name, sig)) {
                                uiOffsets.Add(offset);
                            } else if (IsUnsafeConsumer(nextMem.Name, sig)) {
                                unsafeOffsets.Add(offset);
                            }
                        } catch {}
                    }
                }
            }
        }
    }

    static bool IsUIConsumer(string name, string sig) {
        if (name == "set_Text" || name == "set_HeaderText" || name == "set_ToolTipText" || name == "set_Caption" || name == "set_Title" || name == "set_Description" || name == "set_Filter" || name == "Show" || name == "MsgBox") return true;
        return false;
    }

    static bool IsUnsafeConsumer(string name, string sig) {
        if (name == "set_Name" || name == "GetObject" || name == "GetString" || name == "get_Item" || name == "set_Item" || name == "SelectSingleNode" || name == "SelectNodes" || name == "GetElementsByTagName" || name == "WriteElementString" || name == "WriteAttributeString" || name == "WriteStartElement" || name == "GetAttribute" || name == "Parse" || name == "GetField" || name == "GetMethod" || name == "GetProperty" || name == "GetType" || name == "Load") return true;
        return false;
    }
}
