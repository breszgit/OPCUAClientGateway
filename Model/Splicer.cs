using System;

namespace Splicer_OPCUA.Model
{
    public partial class Splicer
    {
        public string Mrs { get; set; }
        public int CorNo { get; set; }
        public int? Remain { get; set; }
        public int? PreviousRemain { get; set; }
        public DateTime? StampRemain { get; set; }
        public DateTime? StampPreviousRemain { get; set; }
        public int? Status { get; set; }
        public DateTime? LastUpdate { get; set; }
    }
}
