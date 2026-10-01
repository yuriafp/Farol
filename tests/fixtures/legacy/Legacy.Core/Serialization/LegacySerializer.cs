using System.IO;
using System.Runtime.Serialization;
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

        // The swallowed exception leaves a warning (CS0168) every build has shown for years: a diagnostic that exists at load.
        public static object Deserialize(byte[] data)
        {
            try
            {
                using (var stream = new MemoryStream(data))
                {
                    return new BinaryFormatter().Deserialize(stream);
                }
            }
            catch (SerializationException ex)
            {
                return null;
            }
        }
    }
}
