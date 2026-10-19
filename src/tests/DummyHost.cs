
using System;
using System.Threading;
using System.Windows.Forms;

class Program {
    [STAThread]
    static void Main() {
        Application.EnableVisualStyles();
        Form f = new Form();
        f.Text = "Mission Editor";
        MenuStrip ms = new MenuStrip();
        ToolStripMenuItem mFile = new ToolStripMenuItem("&File");
        mFile.DropDownItems.Add(new ToolStripMenuItem("Start New Game"));
        mFile.DropDownItems.Add(new ToolStripMenuItem("Load Saved Game"));
        ToolStripMenuItem mEditor = new ToolStripMenuItem("Scenario Editor");
        ms.Items.Add(mFile);
        ms.Items.Add(mEditor);
        f.Controls.Add(ms);

        Button btn = new Button();
        btn.Text = "Database Viewer";
        f.Controls.Add(btn);

        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
        t.Interval = 2500;
        t.Tick += (s, e) => {
            Console.WriteLine("Form Title: " + f.Text);
            Console.WriteLine("Menu 0: " + mFile.Text);
            Console.WriteLine("SubMenu 0: " + mFile.DropDownItems[0].Text);
            Console.WriteLine("Menu 1: " + mEditor.Text);
            Console.WriteLine("Button: " + btn.Text);
            f.Close();
        };
        t.Start();
        Application.Run(f);
    }
}
