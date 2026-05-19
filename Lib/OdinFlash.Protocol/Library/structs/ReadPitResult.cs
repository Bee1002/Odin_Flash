using OdinFlash.Protocol.Pit;
using System.Collections.Generic;

namespace OdinFlash.Protocol.structs
{
    public struct ReadPitResult
    {
        public bool Result;
        public byte[] data;
        public string error;
        public List<TPIT_Entry> Pit;
    }
}
