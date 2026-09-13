// Usings globaux explicites. Le SDK WPF n'inclut pas System.IO dans ses usings
// implicites, et son import de System.Windows.Shapes rendrait "Path" ambigu :
// on prefere donc controler la liste a la main.

global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;
