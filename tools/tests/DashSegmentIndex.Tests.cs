namespace BiliXbox.Services
{
    public static class DashSegmentIndexTests
    {
        private static int checks;
        private static void Check(bool ok, string why) { ++checks; if (!ok) throw new Exception(why); }
        private static void Put(byte[] b, int p, uint n) { b[p]=(byte)(n>>24); b[p+1]=(byte)(n>>16); b[p+2]=(byte)(n>>8); b[p+3]=(byte)n; }
        private static byte[] Index(bool version1=false)
        {
            var b=new byte[version1?64:56]; Put(b,0,(uint)b.Length);
            b[4]=(byte)'s';b[5]=(byte)'i';b[6]=(byte)'d';b[7]=(byte)'x';b[8]=(byte)(version1?1:0);
            Put(b,12,1);Put(b,16,1000);int p=20;
            if(version1){Put(b,p,1);Put(b,p+4,250);Put(b,p+8,1);Put(b,p+12,10);p+=16;}
            else{Put(b,p,250);Put(b,p+4,10);p+=8;}
            b[p+3]=2;p+=4;
            Put(b,p,100);Put(b,p+4,5000);Put(b,p+8,0x90000000);p+=12;
            Put(b,p,200);Put(b,p+4,4500);Put(b,p+8,0x90000000);return b;
        }
        private static void Reject(byte[] b, ulong start=0, ulong total=ulong.MaxValue)
        {bool failed=false;try{DashSegmentIndex.Parse(b,start,total);}catch(Exception ex)when(ex is InvalidDataException||ex is OverflowException){failed=true;}Check(failed,"malformed SIDX accepted");}
        public static void Run()
        {
            var b=Index();var s=DashSegmentIndex.Parse(b,100,1000);
            Check(s.Version==0&&s.ReferenceId==1&&s.Timescale==1000&&s.EarliestTime==250,"v0 exact timebase");
            Check(s.Segments.Count==2&&s.Segments[0].Start==166&&s.Segments[1].End==465,"absolute index end plus first_offset");
            Check(s.Segments[1].Duration==4500,"last duration retained");
            var v1=DashSegmentIndex.Parse(Index(true),1UL<<32,ulong.MaxValue);
            Check(v1.EarliestTime==(1UL<<32)+250&&v1.Segments[0].Start==(2UL<<32)+74,"v1 timestamps and byte offsets exceed 4GiB without truncation");
            var prefix=new byte[b.Length+8];Put(prefix,0,8);b.CopyTo(prefix,8);
            Check(DashSegmentIndex.Parse(prefix,0,1000).Segments[0].Start==74,"preceding valid box changes absolute offset");
            var rep=XElement.Parse("<Representation xmlns='urn:mpeg:dash:schema:mpd:2011' codecs='hev1.test' width='3840' height='2160'><BaseURL>https://cdn.example.test/signed?x=keep</BaseURL><SegmentBase indexRange='100-155' presentationTimeOffset='250'><Initialization range='0-99'/></SegmentBase></Representation>");
            var ns=rep.Name.Namespace;s.Replace(rep.Element(ns+"SegmentBase"));var list=rep.Element(ns+"SegmentList");
            Check((string)rep.Element(ns+"BaseURL")=="https://cdn.example.test/signed?x=keep"&&(string)rep.Attribute("width")=="3840","signed URL and quality unchanged");
            Check((string)list.Attribute("timescale")=="1000"&&(string)list.Attribute("presentationTimeOffset")=="250","timebase and presentation offset retained");
            Check((string)list.Element(ns+"Initialization").Attribute("range")=="0-99","initialization range unchanged");
            Check((string)list.Element(ns+"SegmentTimeline").Element(ns+"S").Attribute("t")=="250","timeline origin retained");
            Check((string)list.Elements(ns+"SegmentURL").Last().Attribute("mediaRange")=="266-465","exact finite media ranges");
            Reject(new byte[0]);Reject(new byte[DashSegmentIndex.MaximumIndexBytes+1]);
            for(int i=8;i<b.Length;i++){var cut=new byte[i];Array.Copy(b,cut,i);Reject(cut);}
            var bad=Index();bad[8]=2;Reject(bad);
            bad=Index();Put(bad,16,0);Reject(bad);
            bad=Index();bad[31]=0;Reject(bad);
            bad=Index();bad[30]=255;bad[31]=255;Reject(bad);
            bad=Index();Put(bad,32,0x80000064);Reject(bad);
            bad=Index();Put(bad,32,0);Reject(bad);
            bad=Index();Put(bad,36,0);Reject(bad);
            Reject(Index(),0,100);Reject(Index(),ulong.MaxValue-10);
            Console.WriteLine("PASS: "+checks+" SIDX v0/v1, 64-bit offsets, exact timeline/ranges and malformed-index checks.");
        }
    }
}
