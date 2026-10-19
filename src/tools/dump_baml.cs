
using System;
using System.Reflection;
using System.IO;
using System.Collections;
using System.Resources;

class Program {
    static void Main() {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Assembly asm = Assembly.LoadFrom(Path.Combine(gameDir, "Command.exe"));
        
        foreach (string resName in asm.GetManifestResourceNames()) {
            Console.WriteLine("Resource: " + resName);
            if (resName.EndsWith(".g.resources") || resName.Contains("ScenarioFeatures") || resName.Contains("ChooseSide") || resName.Contains("LoadScenario")) {
                Stream s = asm.GetManifestResourceStream(resName);
                ResourceReader rr = new ResourceReader(s);
                foreach (DictionaryEntry de in rr) {
                    Console.WriteLine("  Entry: " + de.Key + " (" + de.Value.GetType().Name + ")");
                    if (de.Key.ToString().ToLower().Contains("scenariofeatures") || de.Key.ToString().ToLower().Contains("scenarioselect")) {
                        MemoryStream ms = de.Value as MemoryStream;
                        if (ms == null && de.Value is Stream) {
                            Stream st = (Stream)de.Value;
                            byte[] buf = new byte[st.Length];
                            st.Read(buf, 0, buf.Length);
                            File.WriteAllBytes(@"projects\汉化\" + de.Key.ToString().Replace("/", "_"), buf);
                            Console.WriteLine("    Saved BAML: " + de.Key);
                        }
                    }
                }
            }
        }
    }
}
