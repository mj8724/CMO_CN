
using System;
using System.Reflection;

class FindCallers {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Module mod = asm.GetModules()[0];
        Type saType = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        MethodInfo strMethod = saType.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        int saToken = strMethod.MetadataToken;

        Type[] types;
        try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }

        int found = 0;
        foreach (Type t in types) {
            if (t == null) continue;
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)) {
                MethodBody body = null;
                try { body = m.GetMethodBody(); } catch {}
                if (body == null) continue;
                byte[] il = body.GetILAsByteArray();
                for (int i = 0; i < il.Length - 4; i++) {
                    if (il[i] == 0x28) {
                        int token = BitConverter.ToInt32(il, i + 1);
                        // Check if it's saToken or MemberRef
                        try {
                            MethodBase mb = mod.ResolveMethod(token);
                            if (mb == strMethod || mb.Name == "iX38Xvt6D") {
                                Console.WriteLine(string.Format("Found caller: {0}.{1} at IL offset {2}, op token: {3:X8}", t.Name, m.Name, i, token));
                                // Print preceding 10 bytes and following 10 bytes
                                int start = Math.Max(0, i - 10);
                                int end = Math.Min(il.Length, i + 15);
                                byte[] slice = new byte[end - start];
                                Array.Copy(il, start, slice, 0, slice.Length);
                                Console.WriteLine("  Context bytes: " + BitConverter.ToString(slice));
                                found++;
                                if (found >= 5) return;
                            }
                        } catch {}
                    }
                }
            }
        }
        Console.WriteLine("Total found: " + found);
    }
}
