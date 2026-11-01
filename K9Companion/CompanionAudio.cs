using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace K9Companion
{
    internal sealed class CompanionAudio : IDisposable
    {
        // Barks (and your whistle) carry a long way; whining, shaking and so on only close by. Breathing has its own.
        private readonly AudioSource source, near, breath;
        // Each sound's takes (sounds/<name>.wav, or <name>_1.wav, <name>_2.wav ...: one is picked at random each time).
        private readonly Dictionary<string,List<AudioClip>> clips = new Dictionary<string,List<AudioClip>>();
        // Made up when there's no recording; the rest only play from their files.
        private static readonly string[] Synthesised={"whistle","bark","whine","happy"}, Recorded={"shake"};
        public CompanionAudio(GameObject dog)
        {
            source=dog.AddComponent<AudioSource>();source.playOnAwake=false;source.spatialBlend=1;
            source.rolloffMode=AudioRolloffMode.Linear;source.minDistance=1;source.maxDistance=Carries;
            near=dog.AddComponent<AudioSource>();near.playOnAwake=false;near.spatialBlend=1;
            near.rolloffMode=AudioRolloffMode.Linear;near.minDistance=1;near.maxDistance=18;
            // The breathing: one loop, always playing, heard only close by (Breathe sets how hard).
            string breathing=Path.Combine(Plugin.AssetFolder,"sounds","breathing.wav");
            if(File.Exists(breathing))
            {
                try
                {
                    AudioClip clip=LoadWav(breathing,"breathing");Level(clip);
                    breath=dog.AddComponent<AudioSource>();breath.playOnAwake=false;breath.loop=true;breath.clip=clip;breath.spatialBlend=1;
                    breath.rolloffMode=AudioRolloffMode.Linear;breath.minDistance=.6f;breath.maxDistance=7;breath.volume=0;
                    resources.Add(clip);
                }
                catch(Exception e) { Plugin.Log.LogWarning("Could not load "+breathing+": "+e.Message); }
            }
            foreach(string name in Synthesised.Concat(Recorded))
            {
                var takes=new List<AudioClip>();
                string folder=Path.Combine(Plugin.AssetFolder,"sounds");
                var paths=new List<string>{Path.Combine(folder,name+".wav")};
                for(int i=1;i<=9;i++) paths.Add(Path.Combine(folder,name+"_"+i+".wav"));
                foreach(string path in paths.Where(File.Exists))
                {
                    try { takes.Add(LoadWav(path,Path.GetFileNameWithoutExtension(path))); }
                    catch(Exception e) { Plugin.Log.LogWarning("Could not load "+path+": "+e.Message); }
                }
                if(takes.Count==0 && Synthesised.Contains(name)) takes.Add(Synth(name));
                // Made-up and recorded sounds all at one loudness (convert_sounds.py levels the recordings the same way).
                foreach(AudioClip take in takes) Level(take);
                if(takes.Count>0) clips[name]=takes;
            }
        }
        private readonly List<AudioClip> resources=new List<AudioClip>();
        private bool reported;

        // How hard the dog breathes now: 0 = not at all (dead, or sounds off), 1 = as loud as it gets; pitch also speeds it up.
        public void Breathe(float effort,float pitch)
        {
            if(breath==null) return;
            Route(breath);
            float volume=Plugin.Sounds.Value?effort*Plugin.Volume.Value*Plugin.BreathingVolume.Value:0;
            // Eased, so it rises and settles like breathing does rather than jumping.
            breath.volume=Mathf.MoveTowards(breath.volume,volume,Time.deltaTime*.25f);
            breath.pitch=Mathf.MoveTowards(breath.pitch,pitch,Time.deltaTime*.2f);
            if(breath.volume>0.0005f && !breath.isPlaying){breath.time=UnityEngine.Random.Range(0f,breath.clip.length*.9f);breath.Play();}
            else if(breath.volume<=0.0005f && breath.isPlaying) breath.Stop();
        }

        // How far off a bark can still be heard, in metres.
        private const float Carries=60f;
        // Heard by whoever else is about (Fika); not the whistle, which is yours.
        internal Action<string> Played;

        public void Play(string name,bool atListener=false)
        {
            if(!atListener) Played?.Invoke(name);
            if(!Plugin.Sounds.Value || !clips.TryGetValue(name,out List<AudioClip> takes)) return;
            AudioSource from=name=="bark"||atListener?source:near;
            Route(from);from.spatialBlend=atListener?0:1;from.PlayOneShot(takes[UnityEngine.Random.Range(0,takes.Count)],Plugin.Volume.Value);
            if(!reported){reported=true;Plugin.Log.LogInfo($"Dog sounds: {clips.Count} kinds loaded, first played '{name}' through {(from.outputAudioMixerGroup!=null?from.outputAudioMixerGroup.name:"no mixer group")}.");}
        }
        private const float Loudness=.12f, Ceiling=.95f;

        // Brings a clip to the shared loudness: measured over the part that sounds, its peaks kept under the ceiling.
        private static void Level(AudioClip clip)
        {
            var data=new float[clip.samples*clip.channels];
            if(data.Length==0 || !clip.GetData(data,0)) return;
            int step=Math.Max(1,clip.frequency/100*clip.channels);
            var blocks=new List<float>();float peak=0,loudest=0;
            for(int i=0;i<data.Length;i+=step)
            {
                float sum=0;int n=Math.Min(step,data.Length-i);
                for(int k=0;k<n;k++){float x=data[i+k];sum+=x*x;peak=Math.Max(peak,Math.Abs(x));}
                float rms=(float)Math.Sqrt(sum/n);blocks.Add(rms);loudest=Math.Max(loudest,rms);
            }
            var heard=blocks.Where(b=>b>loudest*.15f).ToList();
            if(heard.Count==0 || peak<=0) return;
            float level=(float)Math.Sqrt(heard.Sum(b=>b*b)/heard.Count);
            float gain=Math.Min(Loudness/Math.Max(level,1e-6f),Ceiling/peak);
            if(Math.Abs(gain-1)<.02f) return;
            for(int i=0;i<data.Length;i++) data[i]*=gain;
            clip.SetData(data,0);
        }

        private static AudioClip Synth(string name)
        {
            const int rate=22050;float duration=name=="whistle"?.8f:name=="whine"?1.3f:.4f;
            var samples=new float[(int)(rate*duration)];var noise=new System.Random(79);double phase=0;
            for(int i=0;i<samples.Length;i++)
            {
                float t=(float)i/rate,u=t/duration;
                float hz=name=="whistle"?1800+500*Mathf.Sin(u*Mathf.PI):name=="whine"?550+260*Mathf.Sin(u*Mathf.PI):name=="happy"?330+120*u:180-70*u;
                phase+=2*Math.PI*hz/rate;
                float envelope=Mathf.Sin(Mathf.PI*u)*(name=="whistle" && u>.42f && u<.55f?0:1);
                float tone=(float)Math.Sin(phase);
                if(name=="bark") tone=tone*.5f+(float)(noise.NextDouble()*2-1)*.5f;
                samples[i]=tone*envelope*(name=="bark"?.3f:.18f);
            }
            var clip=AudioClip.Create(name,samples.Length,1,rate,false);clip.SetData(samples,0);return clip;
        }
        private static AudioClip LoadWav(string path,string name)
        {
            using(var r=new BinaryReader(File.OpenRead(path)))
            {
                if(new string(r.ReadChars(4))!="RIFF") throw new InvalidDataException("Expected RIFF PCM WAV");
                r.ReadInt32();if(new string(r.ReadChars(4))!="WAVE") throw new InvalidDataException("Expected WAVE");
                int channels=0,rate=0,bits=0,format=0;byte[] data=null;
                while(r.BaseStream.Position+8<=r.BaseStream.Length)
                {
                    string id=new string(r.ReadChars(4));int size=r.ReadInt32();
                    if(size<0 || size>r.BaseStream.Length-r.BaseStream.Position) throw new InvalidDataException("Invalid WAV chunk");
                    long end=r.BaseStream.Position+size;
                    if(id=="fmt " && size>=16) { format=r.ReadUInt16();channels=r.ReadUInt16();rate=r.ReadInt32();r.ReadInt32();r.ReadUInt16();bits=r.ReadUInt16(); }
                    else if(id=="data") { if(size>10000000) throw new InvalidDataException("Sound too large"); data=r.ReadBytes(size); }
                    r.BaseStream.Position=end+(size%2);
                }
                if(format!=1 || bits!=16 || channels<1 || channels>2 || rate<8000 || rate>96000 || data==null || data.Length%(2*channels)!=0)
                    throw new InvalidDataException("Use 16-bit PCM mono/stereo WAV, 8-96 kHz");
                var samples=new float[data.Length/2];for(int i=0;i<samples.Length;i++) samples[i]=BitConverter.ToInt16(data,i*2)/32768f;
                var clip=AudioClip.Create(name,samples.Length/channels,channels,rate,false);clip.SetData(samples,0);return clip;
            }
        }
        // Through the game's own sound mixer, as its sounds are (its volume settings, its mixing); played straight to the
        // speakers, the dog was far louder than the world around it.
        private static void Route(AudioSource s)
        {
            if(s==null||s.outputAudioMixerGroup!=null||!Comfort.Common.Singleton<BetterAudio>.Instantiated) return;
            s.outputAudioMixerGroup=Comfort.Common.Singleton<BetterAudio>.Instance.VeryStandartMixerGroup;
        }

        public void Dispose() { if(breath!=null) UnityEngine.Object.Destroy(breath);foreach(var clip in resources) UnityEngine.Object.Destroy(clip);if(source!=null) UnityEngine.Object.Destroy(source);if(near!=null) UnityEngine.Object.Destroy(near);foreach(var takes in clips.Values) foreach(var clip in takes) UnityEngine.Object.Destroy(clip);clips.Clear(); }
    }
}
