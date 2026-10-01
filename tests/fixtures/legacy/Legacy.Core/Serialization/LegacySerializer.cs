using System.IO;
using System.Runtime.Serialization.Formatters.Binary;

namespace Legacy.Core.Serialization
{
    // BinaryFormatter no longer exists in modern .NET: a realistic migration blocker.
    public static class LegacySerializer
    {
        public static byte[] Serialize(object value)
        {
            using (var stream = new MemoryStream())
            {
                new BinaryFormatter().Serialize(stream, value);
                return stream.ToArray();
            }
        }
    }
}
