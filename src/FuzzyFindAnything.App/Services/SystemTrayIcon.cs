using System.IO;
using System.Windows;
using System.Windows.Media;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace FuzzyFindAnything.App.Services;

internal sealed class SystemTrayIcon : IDisposable
{
    private readonly Drawing.Icon image;
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu;
    private bool disposed;

    public SystemTrayIcon(Action restore, Action quit)
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/FuzzyFindAnything;component/Assets/app.ico"))?.Stream
            ?? throw new FileNotFoundException("The application notification-area icon is missing.");
        using var resource = new Drawing.Icon(stream);
        image = (Drawing.Icon)resource.Clone();
        menu = new Forms.ContextMenuStrip { ShowImageMargin = false, AccessibleName = "FuzzyFindAnything 2.0 tray menu" };
        icon = new Forms.NotifyIcon();
        try
        {
            var open = new Forms.ToolStripMenuItem("Open FuzzyFindAnything 2.0");
            open.Click += (_, _) => restore();
            var exit = new Forms.ToolStripMenuItem("Quit");
            exit.Click += (_, _) => quit();
            menu.Items.AddRange([open, new Forms.ToolStripSeparator(), exit]);
            menu.Opening += (_, _) => ApplyTheme();
            icon.Icon = image;
            icon.Text = "FuzzyFindAnything 2.0";
            icon.ContextMenuStrip = menu;
            icon.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) restore(); };
            icon.MouseDoubleClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) restore(); };
            icon.Visible = true;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void ApplyTheme()
    {
        Drawing.Color Color(string key)
        {
            var color = ((SolidColorBrush)Application.Current.FindResource(key)).Color;
            return Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }
        menu.BackColor = Color("Surface");
        menu.ForeColor = Color("Ink");
        foreach (Forms.ToolStripItem item in menu.Items) item.ForeColor = menu.ForeColor;
        menu.Renderer = new Forms.ToolStripProfessionalRenderer(
            new Palette(menu.BackColor, Color("HoverSurface"), Color("Line")))
        { RoundedEdges = false };
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }

    private sealed class Palette(Drawing.Color background, Drawing.Color selection, Drawing.Color border) : Forms.ProfessionalColorTable
    {
        public override Drawing.Color ToolStripDropDownBackground => background;
        public override Drawing.Color MenuItemSelected => selection;
        public override Drawing.Color MenuItemBorder => border;
        public override Drawing.Color MenuBorder => border;
        public override Drawing.Color SeparatorDark => border;
        public override Drawing.Color SeparatorLight => background;
        public override Drawing.Color ImageMarginGradientBegin => background;
        public override Drawing.Color ImageMarginGradientMiddle => background;
        public override Drawing.Color ImageMarginGradientEnd => background;
    }
}
