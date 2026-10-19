
using System;
using System.IO;
using System.Reflection;
using System.Data.SQLite;

class Program
{
    static void Main()
    {
        string gameDir = @"G:\soft\steam\steamapps\common\Command - Modern Operations";
        Directory.SetCurrentDirectory(gameDir);

        // Load CWDB_514.db3 directly
        string dbPath = Path.Combine(gameDir, "DB", "CWDB_514.db3");
        Console.WriteLine("Testing DB at: " + dbPath + " (exists: " + File.Exists(dbPath) + ")");

        using (var conn = new SQLiteConnection("Data Source=" + dbPath + ";Version=3;Read Only=True;"))
        {
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT ID, Name FROM DataShip WHERE ID = 1478";
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        Console.WriteLine("DB 1478 Ship Name: " + reader["Name"]);
                    }
                    else
                    {
                        Console.WriteLine("DB 1478 Ship NOT FOUND!");
                    }
                }
            }
        }
    }
}
