using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace OriginLibrary.Models
{
    [XmlRoot("DiPManifest")]
    public class GameInstallerData
    {
        public class Launcher
        {
            public string filePath { get; set; }
            public string parameters { get; set; }
            public bool executeElevated { get; set; }
            public bool requires64BitOS { get; set; }
            public bool trial { get; set; }
        }

        public class Runtime
        {
            [XmlElement("launcher")]
            public List<Launcher> launchers { get; set; }
        }

        [XmlElement("runtime")]
        public Runtime runtime { get; set; }

        public class BuildMetaData
        {
            public class GameVersion
            {
                [XmlAttribute("version")]
                public string version { get; set; }
            }

            [XmlElement("gameVersion")]
            public GameVersion gameVersion { get; set; }
        }

        [XmlElement("buildMetaData")]
        public BuildMetaData buildMetaData { get; set; }
    }
}
