namespace NINA.Headless.Models;

public class DomeInfo
{
    public string Name { get; set; } = "Simulator Dome";
    public bool Connected { get; set; }
    public double Azimuth { get; set; }
    public double Altitude { get; set; }
    public bool AtHome { get; set; }
    public bool AtPark { get; set; }
    public string ShutterStatus { get; set; } = "Open";
    public bool Slewing { get; set; }
    public bool Slaved { get; set; }
}
