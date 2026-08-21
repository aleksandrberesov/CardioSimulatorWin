using System;
using System.Reflection;
using System.Linq;

class Program {
    static void Main() {
        try {
            var asm1 = Assembly.LoadFrom(@""E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\HelixToolkit.SharpDX.dll"");
            var asm2 = Assembly.LoadFrom(@""E:\VLN_Project\CardioSimulator\Win\src\CardioSimulator.App\bin\Debug\net8.0-windows10.0.19041.0\win-x64\HelixToolkit.dll"");
            foreach(var asm in new[] {asm1, asm2}) {
                foreach (var t in asm.GetTypes()) {
                    if (t.Name.Contains(""MeshGeometry"") || t.Name.Contains(""Geometry3D"")) {
                        var props = t.GetProperties();
                        foreach(var p in props) {
                            if (p.Name == ""Colors"") {
                                Console.WriteLine($""{t.FullName} has Colors of type {p.PropertyType.FullName}"");
                            }
                        }
                    }
                }
            }
        } catch(Exception e) { Console.WriteLine(e); }
    }
}
