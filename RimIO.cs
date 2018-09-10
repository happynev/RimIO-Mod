using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using HugsLib;
using UnityEngine;
using HugsLib.Settings;
using System.Text.RegularExpressions;
using System.Net.Sockets;
using System.Net;
using System.Text;
using System.Diagnostics;
using System;
using System.Xml.Serialization;
using System.IO;
using Verse.AI;
using System.Threading;

namespace RimIO
{

    public class Settings
    {
        public SettingHandle<bool> enableSending;
        public SettingHandle<bool> enableWorld;
        public SettingHandle<bool> enablePawns;
        public SettingHandle<bool> enableSkills;
        public SettingHandle<bool> enableJobs;
        public SettingHandle<string> ipAddress;
        public SettingHandle<int> port;
        public SettingHandle<bool> enableDebug;

        internal string getBaseHttpUrl()
        {
            return "http://" + ipAddress + ":" + port + "/";
        }
        
        internal string getDataUrl()
        {
            return getBaseHttpUrl() + "/GameData";
        }

        internal string getAssetUrl()
        {
            return getBaseHttpUrl() + "/Assets";
        }

        public Dictionary<String, Texture2D> pawnPortraits = new Dictionary<string, Texture2D>();
    }

    public class TickDataHolder
    {
        public HashSet<String> selectedPawns = new HashSet<String>();
    }
    
    public class RimIO : ModBase
    {
        private static Dictionary<int, Map> knownMaps = new Dictionary<int, Map>();

        public override string ModIdentifier => "RimLogger";
        private Settings settings = new Settings();
        public override void DefsLoaded()
        {
            base.DefsLoaded();
            settings.enableSending = Settings.GetHandle<bool>("enableSending", "send", "toggleSetting_desc", true);
            settings.enableWorld = Settings.GetHandle<bool>("enableWorld", "world", "toggleSetting_desc", true);
            settings.enablePawns = Settings.GetHandle<bool>("enablePawns", "pawns", "toggleSetting_desc", true);
            settings.enableSkills = Settings.GetHandle<bool>("enableSkills", "skills", "toggleSetting_desc", true);
            settings.enableJobs = Settings.GetHandle<bool>("enableJobs", "jobs", "toggleSetting_desc", true);
            settings.enableDebug = Settings.GetHandle<bool>("enableDebug", "debug", "toggleSetting_desc", true);
            settings.ipAddress = Settings.GetHandle<string>("ipAddress", "ip address/hostname", "toggleSetting_desc", "localhost");
            settings.port = Settings.GetHandle<int>("port", "toggleSetting_title", "port", 5500);
        }

        private void ClearPersistentData()
        {
            knownMaps.Clear();
        }

        public override void MapDiscarded(Map map)
        {
            base.MapDiscarded(map);
            if (knownMaps.ContainsValue(map))
            {
                var item = knownMaps.First(kvp => kvp.Value.Equals(map));
                knownMaps.Remove(item.Key);
            }
        }

        public override void MapLoaded(Map map)
        {
            base.MapLoaded(map);
            Messages.Message(new Message("rimlogger map loaded", MessageTypeDefOf.CautionInput));
            knownMaps.Add(map.uniqueID, map);
        }

        public override void WorldLoaded()
        {
            base.WorldLoaded();
            ClearPersistentData();
        }

        public override void Tick(int currentTick)
        {
            base.Tick(currentTick);
            if (settings.enableSending && currentTick % 60 == 1) //once per second on normal speed
            {
                updatePawnPortraits();
                Thread sender = new Thread(unused => SendAsyncGameData(currentTick, settings));
                sender.Start();
            }
        }

        private void updatePawnPortraits()
        {
            Stopwatch t = new Stopwatch();
            t.Start();
            List<Pawn> pawns = Find.ColonistBar.GetColonistsInOrder();
            settings.pawnPortraits.Clear();
            foreach (Pawn p in pawns)
            {
                if (p != null)
                {
                    settings.pawnPortraits.Add(p.ThingID, getPawnPortrait(p));
                }
            }
            t.Stop();
            if (settings.enableDebug)
            {
                Messages.Message(new Message("copied " + settings.pawnPortraits.Count + " portraits in " + t.ElapsedMilliseconds + "ms", MessageTypeDefOf.SilentInput));
            }
        }

        private static void SendAsyncGameData(int currentTick, Settings settings)
        {
            Stopwatch t = new Stopwatch();
            t.Start();
            string logmsg = "rimlogger tick " + currentTick;
            string msg = CollectGameState(settings);
            var request = (HttpWebRequest)WebRequest.Create(settings.getDataUrl());
            request.ContentType = "application/xml";
            request.Accept = "application/xml";
            request.Method = "POST";
            request.KeepAlive = true;
            request.Timeout = 1000;
            request.Headers.Add("X-RimIODataVersion", "1");
            request.ReadWriteTimeout = 1000;
            try
            {
                using (var streamWriter = new StreamWriter(request.GetRequestStream()))
                {
                    streamWriter.Write(msg);
                }
                request.GetResponse().Close(); //don't care about response, but needed to release the request
            }
            catch (Exception e)
            {
                Messages.Message(new Message("RimIO failed to POST to " + settings.getBaseHttpUrl() + " -->" + e.Message, MessageTypeDefOf.RejectInput));
                Messages.Message(new Message("Either fix or disable in Mod Settings or start the RimIO Companion App", MessageTypeDefOf.RejectInput));
            }
            t.Stop();
            if (settings.enableDebug)
            {
                Messages.Message(new Message(logmsg + " built and sent in " + t.ElapsedMilliseconds + "ms for " + msg.Length + " bytes", MessageTypeDefOf.SilentInput));
            }
        }

        private Texture2D getPawnPortrait(Pawn p)
        {
            RenderTexture tmpPortrait = PortraitsCache.Get(p, ColonistBarColonistDrawer.PawnTextureSize, ColonistBarColonistDrawer.PawnTextureCameraOffset, 1.28205f);
            RenderTexture.active = tmpPortrait;
            Texture2D tempTex = new Texture2D(tmpPortrait.width, tmpPortrait.height);
            tempTex.ReadPixels(new Rect(0, 0, tmpPortrait.width, tmpPortrait.height), 0, 0);
            tempTex.Apply();
            return tempTex;
        }

        private static string CollectGameState(Settings settings)
        {
            //i have NO idea if it is a good idea to do this in a seperate Thread. but so far no trouble
            TickDataHolder tickData = new TickDataHolder();
            GameData gsm = new GameData
            {
                tick = GenTicks.TicksGame,
                worldSeed = Find.World.info.seedString,
                includesMaps = settings.enableWorld,
                includesPawns = settings.enablePawns,
            };
            if (settings.enableWorld)
            {
                foreach (Map m in knownMaps.Values)
                {
                    gsm.maps.Add(new MapData(m, settings));
                }
            }
            if (settings.enablePawns)
            {
                foreach (object o in Find.MapUI.selector.SelectedObjectsListForReading)
                {
                    if (o is Pawn p)
                    {
                        tickData.selectedPawns.Add(p.ThingID);
                    }
                }
                foreach (Pawn p in Find.ColonistBar.GetColonistsInOrder())
                {
                    if (p != null)
                    {
                        gsm.colonists.Add(new PawnData(p, tickData, settings));
                    }
                }
            }
            XmlSerializer serializer = new XmlSerializer(typeof(GameData));
            StringWriter writer = new StringWriter();
            serializer.Serialize(writer, gsm);
            writer.Close();
            return writer.ToString();
        }
    }

    [Serializable]
    public class GameData
    {
        public long tick;
        public string worldSeed;
        public bool includesMaps;
        public bool includesPawns;
        public List<MapData> maps = new List<MapData>();
        public List<PawnData> colonists = new List<PawnData>();
        public List<PawnData> visitors = new List<PawnData>();
        public List<PawnData> enemies = new List<PawnData>();
    }

    [Serializable]
    public class MapData
    {
        public string id;
        public string name;
        public bool colony;
        public int sizeX;
        public int sizeY;
        public float wealthFloors;
        public float wealthBuildings;
        public float wealthItems;
        public float wealthPawns;
        public float wealthTotal;
        public MapData() { }
        public MapData(Map m, Settings settings)
        {
            id = m.uniqueID.ToString();
            colony = m.IsPlayerHome;
            sizeX = m.info.Size.x;
            sizeY = m.info.Size.z;
            wealthFloors = m.wealthWatcher.WealthFloorsOnly;
            wealthBuildings = m.wealthWatcher.WealthBuildings - wealthFloors;
            wealthItems = m.wealthWatcher.WealthItems;
            wealthPawns = m.wealthWatcher.WealthPawns;
            wealthTotal = m.wealthWatcher.WealthTotal;
        }
    }

    [Serializable]
    public class PawnData
    {
        public string id;
        public string fullName;
        public string nickName;
        public string label;
        public bool includesSkills;
        public bool includesNeeds;
        public bool includesHealth;
        public bool includesJob;
        public bool includesPortrait;
        public int onMap;
        public bool colonist;
        public bool visitor;
        public bool prisoner;
        public bool enemy;
        public bool drafted;
        public bool selected;
        public float age;
        public float currentHealth;
        public bool dead;
        public bool downed;
        public bool sleeping;
        public bool idle;
        public bool medicalRest;
        public bool inMentalState;
        public bool inAggroMentalState;
        public Location location;
        public List<string> traits = new List<string>();
        public PawnSkills skillsData;
        public PawnNeeds needData;
        public PawnHealth healthData;
        public PawnCapacity capacityData;
        public JobData job;
        public byte[] portrait;
        public PawnData() { }
        public PawnData(Pawn p, TickDataHolder tickData, Settings settings)
        {
            id = p.ThingID;
            fullName = p.Name.ToString();
            nickName = p.LabelShort;
            label = p.Label;
            includesSkills = settings.enableSkills;
            includesNeeds = true;
            includesHealth = true;
            includesJob = true;
            includesPortrait = true;

            if (p.Map != null)
            {
                onMap = p.Map.uniqueID;
            }
            else
            {
                onMap = -1;
            }
            colonist = p.IsColonist;
            //visitor = p.guest != null && p.guest.HostFaction.IsPlayer;
            prisoner = p.IsPrisonerOfColony;
            drafted = p.Drafted;
            selected = tickData.selectedPawns.Contains(id);
            age = p.ageTracker.AgeBiologicalYearsFloat;
            currentHealth = p.health.summaryHealth.SummaryHealthPercent;
            dead = p.Dead;
            downed = p.Downed;
            sleeping = p.CurJob != null && p.jobs.curDriver.asleep;
            idle = p.mindState.IsIdle;
            medicalRest = p.InBed() && p.CurrentBed().Medical;
            inAggroMentalState = p.InAggroMentalState;
            inMentalState = p.InMentalState;
            location = new Location(p.Position);
            if (p.story != null && p.story.traits != null)
            {
                foreach (Trait t in p.story.traits.allTraits)
                {
                    traits.Add(t.Label);
                }
            }
            if (includesSkills && p.skills != null)
            {
                skillsData = new PawnSkills(p.skills, settings);
            }
            if (includesNeeds && p.needs != null)
            {
                needData = new PawnNeeds(p.needs, settings);
            }
            if (includesHealth && p.health != null)
            {
                healthData = new PawnHealth(p, p.health, settings);
                capacityData = new PawnCapacity(p.health.capacities);
            }
            if (includesJob && p.CurJob != null)
            {
                job = new JobData(p, p.CurJob);
            }
            if (includesPortrait && settings.pawnPortraits.ContainsKey(p.ThingID))
            {
                portrait = settings.pawnPortraits[p.ThingID].EncodeToPNG();
            }
        }
    }

    [Serializable]
    public class JobData
    {
        public string name;
        public JobTarget targetA;
        public JobTarget targetB;
        public JobTarget targetC;
        public JobData() { }
        public JobData(Pawn p, Job job)
        {
            name = job.def.reportString;
            if (job.targetA != null)
            {
                targetA = new JobTarget(job.targetA);
            }
            if (job.targetB != null)
            {
                targetB = new JobTarget(job.targetB);
            }
            if (job.targetC != null)
            {
                targetC = new JobTarget(job.targetC);
            }
        }
    }
    [Serializable]
    public class JobTarget
    {
        public string name;
        public Location location;
        public JobTarget() { }

        public JobTarget(LocalTargetInfo t)
        {
            if (t.HasThing)
            {
                name = t.Thing.Label;
            }
            else
            {
                name = "?" + t.ToString();
            }
            location = new Location(t.Cell);
        }
    }
    [Serializable]
    public class Location
    {
        public int x;
        public int y;
        public Location() { }
        public Location(IntVec3 p)
        {
            x = p.x;
            y = p.z; //i know.... lazy
        }
    }


    [Serializable]
    public class PawnNeeds
    {
        public List<KeyValuePair> needs = new List<KeyValuePair>();
        public PawnNeeds() { }
        public PawnNeeds(Pawn_NeedsTracker n, Settings settings)
        {
            foreach (Need need in n.AllNeeds)
            {
                needs.Add(new KeyValuePair(need.LabelCap, "" + need.CurLevelPercentage));
            }
        }
    }

    [Serializable]
    public class PawnHealth
    {
        //public List<BodyPartData> bodyParts = new List<BodyPartData>();
        public List<HediffData> hediffs = new List<HediffData>();
        public PawnHealth() { }

        public PawnHealth(Pawn p, Pawn_HealthTracker h, Settings settings)
        {
            foreach (Hediff hd in h.hediffSet.hediffs)
            {
                hediffs.Add(new HediffData(hd));
            }
        }
    }

    public class HediffData
    {
        public string label;
        public bool tendable;
        public bool tended;
        public float bleedRate;
        public float pain;
        public string location;
        public float healthPercentImpact;
        public bool permanent;
        public HediffData() { }

        public HediffData(Hediff hd)
        {
            label = hd.Label;
            tendable = hd.TendableNow();
            tended = hd.IsTended();
            bleedRate = hd.BleedRate;
            pain = hd.PainOffset;
            if (hd.Part != null)
            {
                location = hd.Part.Label;
            }
            healthPercentImpact = hd.SummaryHealthPercentImpact;
            permanent = hd.IsPermanent();
        }
    }

    public class BodyPartData
    {
        public string label;
        public BodyPartData() { }

        public BodyPartData(Hediff hd)
        {
            label = hd.Label;

        }
    }


    [Serializable]
    public class PawnCapacity
    {
        public List<KeyValuePair> capacities = new List<KeyValuePair>();
        public PawnCapacity() { }
        public PawnCapacity(PawnCapacitiesHandler c)
        {
            capacities.Add(new KeyValuePair("BloodFiltration", "" + c.GetLevel(PawnCapacityDefOf.BloodFiltration)));
            capacities.Add(new KeyValuePair("BloodPumping", "" + c.GetLevel(PawnCapacityDefOf.BloodPumping)));
            capacities.Add(new KeyValuePair("Breathing", "" + c.GetLevel(PawnCapacityDefOf.Breathing)));
            capacities.Add(new KeyValuePair("Consciousness", "" + c.GetLevel(PawnCapacityDefOf.Consciousness)));
            capacities.Add(new KeyValuePair("Eating", "" + c.GetLevel(PawnCapacityDefOf.Eating)));
            capacities.Add(new KeyValuePair("Hearing", "" + c.GetLevel(PawnCapacityDefOf.Hearing)));
            capacities.Add(new KeyValuePair("Manipulation", "" + c.GetLevel(PawnCapacityDefOf.Manipulation)));
            capacities.Add(new KeyValuePair("Metabolism", "" + c.GetLevel(PawnCapacityDefOf.Metabolism)));
            capacities.Add(new KeyValuePair("Moving", "" + c.GetLevel(PawnCapacityDefOf.Moving)));
            capacities.Add(new KeyValuePair("Sight", "" + c.GetLevel(PawnCapacityDefOf.Sight)));
            capacities.Add(new KeyValuePair("Talking", "" + c.GetLevel(PawnCapacityDefOf.Talking)));
        }
    }

    [Serializable]
    public class KeyValuePair
    {
        public string key;
        public string value;
        public KeyValuePair() { }
        public KeyValuePair(string _key, string _value)
        {
            key = _key;
            value = _value;
        }
    }

    [Serializable]
    public class PawnSkills
    {
        public List<SkillData> skills = new List<SkillData>();
        public PawnSkills() { }
        public PawnSkills(Pawn_SkillTracker pawnskills, Settings settings)
        {
            foreach (SkillRecord sr in pawnskills.skills)
            {
                skills.Add(new SkillData(sr, settings));
            }
        }
    }

    [Serializable]
    public class SkillData
    {
        public string name;
        public string passion;
        public int level;
        public bool enabled;
        public float xpProgress;
        public float totalXp;
        public float currentXp;
        public float levelupXp;
        public SkillData() { }
        public SkillData(SkillRecord skillRecord, Settings settings)
        {
            name = skillRecord.def.LabelCap;
            passion = skillRecord.passion.ToString();
            level = skillRecord.levelInt;
            enabled = !skillRecord.TotallyDisabled;
            xpProgress = skillRecord.XpProgressPercent;
            totalXp =skillRecord.XpTotalEarned;
            currentXp = skillRecord.xpSinceLastLevel;
            levelupXp = skillRecord.XpRequiredForLevelUp;
        }
    }

    
    [Serializable]
    public class ItemData
    {
        public string name;
        public string quality;
        public float durability;
        public float value;
    }

    [Serializable]
    public class WeaponData : ItemData
    {
    }

    [Serializable]
    public class ApparelData : ItemData
    {
        List<string> coveredLocations = new List<string>();
    }


    public static class Tools
    {
        public static void TicksToPeriod(this long numTicks, out int years, out int seasons, out int days, out float hoursFloat)
        {
            years = (int)(numTicks / 3600000L);
            long num = numTicks - (long)years * 3600000L;
            seasons = (int)(num / 900000L);
            num -= (long)seasons * 900000L;
            days = (int)(num / 60000L);
            num -= (long)days * 60000L;
            hoursFloat = (float)num / 2500f;
        }

    }

}
