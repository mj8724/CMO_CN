
using System;
using System.Reflection;
using System.Collections.Generic;

class TestHijack {
    static void Main() {
        Assembly asm = Assembly.LoadFile(@"G:\soft\steam\steamapps\common\Command - Modern Operations\Command.exe");
        Type t = asm.GetType("AoIBWWlDJbaf7LijnA.oMu6jVbdhHEH79DDhU");
        MethodInfo method = t.GetMethod("iX38Xvt6D", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        
        // 1. Call iX38Xvt6D(0) to trigger native SA initialization
        string str0 = (string)method.Invoke(null, new object[] { 0 });
        Console.WriteLine("Native str(0): " + str0);

        // 2. Inspect static fields
        FieldInfo f_ad6 = t.GetField("ad6YFOZX46s", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo f_OVo = t.GetField("OVoYFvH0GRf", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo f_vWj = t.GetField("vWjYFIwuiK7", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo f_lock = t.GetField("rPCYFK5Hgnc", BindingFlags.Static | BindingFlags.NonPublic);

        byte[] rawBytes = (byte[])f_ad6.GetValue(null);
        List<string> strList = (List<string>)f_OVo.GetValue(null);
        List<int> offList = (List<int>)f_vWj.GetValue(null);
        object lockObj = f_lock.GetValue(null);

        Console.WriteLine(string.Format("RawBytes len: {0}, StrList count: {1}, OffList count: {2}", rawBytes.Length, strList.Count, offList.Count));

        // 3. Test hijacking offset 96
        int targetOffset = 96;
        string orig96 = (string)method.Invoke(null, new object[] { targetOffset });
        Console.WriteLine("Orig str(96): " + orig96);

        // Modify in place
        lock (lockObj) {
            int idx = BitConverter.ToInt32(rawBytes, targetOffset);
            if (idx < offList.Count && offList[idx] == targetOffset) {
                strList[idx] = "[测试汉化] 登录凭据";
            }
        }

        // Call again
        string new96 = (string)method.Invoke(null, new object[] { targetOffset });
        Console.WriteLine("Hijacked str(96): " + new96);

        // 4. Test pre-injecting an offset that was NEVER accessed yet: say offset 272
        int targetOffset2 = 272;
        lock (lockObj) {
            int newIdx = strList.Count;
            strList.Add("[测试汉化] 蒸汽认证令牌");
            offList.Add(targetOffset2);
            byte[] idxBytes = BitConverter.GetBytes(newIdx);
            Array.Copy(idxBytes, 0, rawBytes, targetOffset2, 4);
        }

        // Call iX38Xvt6D(272) for the very first time!
        string new272 = (string)method.Invoke(null, new object[] { targetOffset2 });
        Console.WriteLine("Pre-injected first-access str(272): " + new272);
    }
}
