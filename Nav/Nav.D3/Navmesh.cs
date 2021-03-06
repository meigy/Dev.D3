﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Timers;
using System.IO;
using System.Linq;
using System.Threading;
using Enigma;
using Enigma.D3;
using Enigma.D3.Memory;
using Enigma.D3.Assets;
using Enigma.D3.Enums;
using Enigma.D3.Helpers;
using System.Diagnostics;

namespace Nav.D3
{
    public class Navmesh : Nav.Navmesh
    {
        public Navmesh(Engine engine, ExplorationEngine explorator, bool verbose = false)
            : base(explorator, verbose)
        {
            using (new Profiler("[Nav.D3] Loading SNO data cache took {t}."))
                LoadSnoCache();

            m_Engine = engine;
            m_FetchNavDataTimer.AutoReset = false;
            m_FetchNavDataTimer.Interval = 300;
            m_FetchNavDataTimer.Elapsed += new ElapsedEventHandler(FetchData);
            m_FetchNavDataTimer.Start();

            DangerRegionsEnabled = false;

            m_FetchDangerRegionsTimer.AutoReset = false;
            m_FetchDangerRegionsTimer.Interval = 100;
            m_FetchDangerRegionsTimer.Elapsed += new ElapsedEventHandler(FetchDangerRegions);
            m_FetchDangerRegionsTimer.Start();            

            Log("[Nav.D3] Navmesh created!");
        }

        public override void Dispose()
        {
            base.Dispose();
            Stop();
        }

        public override void Clear()
        {
            base.Clear();

            using (new WriteLock(D3InputLock))
            {
                m_AllowedAreasSnoId.Clear();
                m_AllowedGridCellsId.Clear();
            }

            using (new WriteLock(ProcessedScenesLock))
            {
                m_ProcessedSceneId.Clear();
            }

            Log("[Nav.D3] Navmesh cleared!");
        }

        protected override void OnSerialize(BinaryWriter w)
        {
            using (new ReadLock(D3InputLock))
            using (new ReadLock(ProcessedScenesLock))
            {
                w.Write(m_AllowedAreasSnoId.Count);
                foreach (int area_sno_id in m_AllowedAreasSnoId)
                    w.Write(area_sno_id);

                w.Write(m_AllowedGridCellsId.Count);
                foreach (int grid_cell_id in m_AllowedGridCellsId)
                    w.Write(grid_cell_id);

                w.Write(m_ProcessedSceneId.Count);
                foreach (SceneData.uid scene_id in m_ProcessedSceneId)
                    scene_id.Serialize(w);

                base.OnSerialize(w);
            }
        }

        protected override void OnDeserialize(BinaryReader r)
        {
            using (new WriteLock(D3InputLock))
            using (new WriteLock(ProcessedScenesLock))
            {
                m_AllowedAreasSnoId.Clear();
                m_AllowedGridCellsId.Clear();

                int area_sno_id_count = r.ReadInt32();

                for (int i = 0; i < area_sno_id_count; ++i)
                {
                    int area_sno_id = r.ReadInt32();
                    m_AllowedAreasSnoId.Add(area_sno_id);
                }

                int grid_cell_id_count = r.ReadInt32();

                for (int i = 0; i < grid_cell_id_count; ++i)
                {
                    int grid_cell_id = r.ReadInt32();
                    m_AllowedGridCellsId.Add(grid_cell_id);
                }
            
                m_ProcessedSceneId.Clear();

                int scene_id_count = r.ReadInt32();

                for (int i = 0; i < scene_id_count; ++i)
                    m_ProcessedSceneId.Add(new SceneData.uid(r));

                base.OnDeserialize(r);
            }            
        }

        public static string SceneSnoCacheDir
        {
            get { return SCENE_SNO_CACHE_DIR; }
        }

        public bool DangerRegionsEnabled { get; set; }

        public double FetchNavDataInterval
        {
            set { m_FetchNavDataTimer.Interval = value; }
        }

        public bool IsUpdating
        {
            get
            {
                try
                {
                    return IsLocalActorValid() && IsObjectManagerOnNewFrame();
                }
                catch (Exception)
                {
                }

                return false;
            }
        }

        public List<int> AllowedAreasSnoId
        {
            get { using (new ReadLock(D3InputLock)) return new List<int>(m_AllowedAreasSnoId); }
            set { using (new WriteLock(D3InputLock)) m_AllowedAreasSnoId = new List<int>(value); }
        }

        public List<int> AllowedGridCellsId
        {
            get { using (new ReadLock(D3InputLock)) return new List<int>(m_AllowedGridCellsId); }
            set { using (new WriteLock(D3InputLock)) m_AllowedGridCellsId = new List<int>(value); }
        }

        public static Navmesh Create(Engine engine, ExplorationEngine explorator = null, bool verbose = false)
        {
            return Current = new Navmesh(engine, explorator, verbose);
        }

        public static Navmesh Current { get; private set; }

        public void Start()
        {
            if (!m_FetchNavDataTimer.Enabled)
                m_FetchNavDataTimer.Start();
            if (!m_FetchDangerRegionsTimer.Enabled)
                m_FetchDangerRegionsTimer.Start();
        }

        public void Stop()
        {
            // wait for in progress fetch to finish
            lock (m_FetchNavDataLock)
                m_FetchNavDataTimer.Stop();
            lock (m_FetchDangerRegionsLock)
                m_FetchDangerRegionsTimer.Stop();
        }

        private void FetchData(object source = null, ElapsedEventArgs e = null)
        {
            lock (m_FetchNavDataLock)
            {
                if (IsUpdating)
                {
                    try
                    {
                        FetchSceneSnoData();
                        using (new WriteLock(ProcessedScenesLock))
                        {
                            FetchSceneData();
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                m_FetchNavDataTimer.Start();
            }            
        }

        private void FetchSceneSnoData()
        {
            List<SceneSnoNavData> new_scene_sno_nav_data = new List<SceneSnoNavData>();

            //using (new Profiler("[Nav.D3.Navigation] Scene sno data aquired [{t}]", 70))
            {
                var sno_scenes = SnoMemoryHelper.Enumerate<Enigma.D3.Assets.Scene>(SnoGroupId.Scene);

                foreach (Enigma.D3.Assets.Scene sno_scene in sno_scenes)
                {
                    if (sno_scene == null ||
                        sno_scene.x000_Header.x00_SnoId <= 0 ||
                        m_SnoCache.ContainsKey(sno_scene.x000_Header.x00_SnoId))
                    {
                        continue;
                    }

                    new_scene_sno_nav_data.Add(new SceneSnoNavData(sno_scene));
                }
            }

            //using (new Nav.Profiler("[Nav.D3.Navigation] Scene sno data added [{t}]"))
            {
                // add and save new data later to reduce memory reading duration
                foreach (SceneSnoNavData data in new_scene_sno_nav_data)
                {
                    m_SnoCache.Add(data.SceneSnoId, data);
                    data.Save();

                    Log("[Nav.D3] SceneSnoId " + data.SceneSnoId + " added to cache, now containing " + m_SnoCache.Count + " entries!");
                }
            }
        }

        private void FetchSceneData()
        {
            List<SceneData> new_scene_data = new List<SceneData>();

            //using (new Nav.Profiler("[Nav.D3.Navigation] Navmesh data aquired [{t}]", 70))
            {
                foreach (Enigma.D3.Scene scene in m_Engine.ObjectManager.x998_Scenes.Dereference())
                {
                    if (scene == null || scene.x000_Id < 0)
                        continue;
                    
                    SceneData scene_data = new SceneData(scene);

                    if (m_AllowedAreasSnoId.Count > 0 && !m_AllowedAreasSnoId.Contains(scene_data.AreaSnoId) && !m_AllowedGridCellsId.Contains(scene_data.SceneSnoId))
                        continue;

                    if (m_ProcessedSceneId.Contains(scene_data.SceneUid))
                        continue;

                    new_scene_data.Add(scene_data);
                }
            }

            //using (new Nav.Profiler("[Nav.D3.Navigation] Navmesh data added [{t}]"))
            {
                int grid_cells_added = 0;

                foreach (SceneData scene_data in new_scene_data)
                {
                    SceneSnoNavData sno_nav_data = null;

                    // allow empty grids
                    m_SnoCache.TryGetValue(scene_data.SceneSnoId, out sno_nav_data);
                    
                    GridCell grid_cell = new GridCell(scene_data.Min, scene_data.Max, scene_data.SceneSnoId, scene_data.AreaSnoId);
                    grid_cell.UserData = scene_data.AreaSnoId;

                    if (sno_nav_data != null)
                    {
                        int cell_id = 0;

                        foreach (Cell cell in sno_nav_data.Cells)
                            grid_cell.Add(new Cell(cell.Min + scene_data.Min, cell.Max + scene_data.Min, cell.Flags, cell_id++));
                    }

                    if (Add(grid_cell, false))
                        ++grid_cells_added;
                        
                    m_ProcessedSceneId.Add(scene_data.SceneUid);
                }

                if (grid_cells_added > 0)
                {
                    Log("[Nav.D3] " + grid_cells_added + " grid cells added" + (Explorator == null ? " (EXPLORATOR NOT PRESENT!!!)" : "") + ".");

                    if (Explorator != null)
                        Explorator.OnNavDataChange();
                }
            }
        }

        class danger_data
        {
            public danger_data(string name, float range, float move_cost_mult)
            {
                this.name = name;
                this.range = range;
                this.move_cost_mult = move_cost_mult;
            }

            public string name;
            public float range;
            public float move_cost_mult;
        }

        private static readonly List<danger_data> DANGERS = new List<danger_data>() { new danger_data("sporeCloud_emitter", 15, 3),
                                                                                      new danger_data("ChargedBolt_Projectile", 7, 3),
                                                                                      new danger_data("monsterAffix_Desecrator_damage_AOE", 10, 3),
                                                                                      new danger_data("monsterAffix_Plagued", 15, 3),
                                                                                      new danger_data("monsterAffix_Molten_trail", 7, 3),
                                                                                      new danger_data("monsterAffix_Molten_death", 20, 3),
                                                                                      new danger_data("arcaneEnchantedDummy_spawn", 35, 3),
                                                                                      new danger_data("MonsterAffix_ArcaneEnchanted_PetSweep", 35, 3),
                                                                                      new danger_data("monsterAffix_frozen_iceClusters", 20, 3),
                                                                                      new danger_data("MonsterAffix_Orbiter", 7, 3),
                                                                                      new danger_data("MonsterAffix_frozenPulse", 15, 3),
                                                                                      new danger_data("MonsterAffix_CorpseBomber", 15, 3),
                                                                                      new danger_data("MorluSpellcaster_Meteor_Pending", 25, 3),
                                                                                      new danger_data("_Generic_AOE_", 25, 3),
                                                                                      new danger_data("ZoltunKulle_EnergyTwister", 20, 3),
                                                                                      new danger_data("Gluttony_gasCloud", 25, 3),
                                                                                      new danger_data("UberMaghda_Punish_", 20, 3),
                                                                                      new danger_data("Random_FallingRocks", 40, 3),
                                                                                      new danger_data("ringofFire_damageArea", 35, 3),
                                                                                      new danger_data("BoneCage_Proxy", 20, 3),
                                                                                      new danger_data("Brute_leap_telegraph", 20, 3),
                                                                                      new danger_data("creepMobArm", 20, 3),
                                                                                      new danger_data("Morlu_GroundBomb", 40, 3),
                                                                                      new danger_data("grenadier_proj_trail", 40, 3),
                                                                                      new danger_data("orbOfAnnihilation", 40, 3),
                                                                                      //new danger_data("westmarchRanged_projectile", 15, 1.5f),
                                                                                      new danger_data("Corpulent_A", 25, 3) };

        private void FetchDangerRegions(object source = null, ElapsedEventArgs e = null)
        {
            lock (m_FetchDangerRegionsLock)
            {
                if (IsUpdating && DangerRegionsEnabled)
                {
                    try
                    {
                        IEnumerable<ActorCommonData> objects = ActorCommonDataHelper.Enumerate(x => (x.x184_ActorType == ActorType.ServerProp || x.x184_ActorType == ActorType.Monster || x.x184_ActorType == ActorType.Projectile || x.x184_ActorType == ActorType.CustomBrain) && DANGERS.Exists(d => x.x004_Name.Contains(d.name)));

                        HashSet<region_data> dangers = new HashSet<region_data>();

                        foreach (ActorCommonData obj in objects)
                        {
                            danger_data data = DANGERS.Find(d => obj.x004_Name.Contains(d.name));
                            if (data != null)
                            {
                                Vec3 pos = new Vec3(obj.x0D0_WorldPosX, obj.x0D4_WorldPosY, obj.x0D8_WorldPosZ);
                                AABB area = new AABB(pos - new Vec3(data.range, data.range, pos.Z - 100), pos + new Vec3(data.range, data.range, pos.Z + 100));
                                dangers.Add(new region_data(area, data.move_cost_mult));
                            }
                        }

                        Regions = dangers;
                    }
                    catch (Exception)
                    {
                    }
                }

                m_FetchDangerRegionsTimer.Start();
            }
        }

        private bool IsLocalActorValid()
        {
            if (m_Engine == null)
                return false;

            m_LocalData = m_LocalData ?? m_Engine.LocalData;

            byte is_not_in_game = (byte)m_LocalData.x04_IsNotInGame;
            if (is_not_in_game == 0xCD) // structure is being updated, everything is cleared with 0xCD ('-')
            {
                if (!m_IsLocalActorReady)
                    return false;
            }
            else
            {
                if (is_not_in_game == 0)
                {
                    if (!m_IsLocalActorReady)
                        m_IsLocalActorReady = true;
                }
                else
                {
                    if (m_IsLocalActorReady)
                        m_IsLocalActorReady = false;

                    return false;
                }
            }

            return m_LocalData.x00_IsActorCreated == 1;
        }

        private bool IsObjectManagerOnNewFrame()
        {
            if (m_Engine == null)
                return false;

            m_ObjectManager = m_ObjectManager ?? m_Engine.ObjectManager;

            // Don't do anything unless game updated frame.
            int currentFrame = m_ObjectManager.x038_Counter_CurrentFrame;

            if (currentFrame == m_LastFrame)
                return false;

            if (currentFrame < m_LastFrame)
            {
                // Lesser frame than before = left game probably.
                m_LastFrame = currentFrame;
                return false;
            }

            m_LastFrame = currentFrame;
            return true;
        }

        private void LoadSnoCache()
        {
            if (!USE_SNO_CACHE)
                return;

            if (!Directory.Exists(SCENE_SNO_CACHE_DIR))
            {
                Directory.CreateDirectory(SCENE_SNO_CACHE_DIR);
                return;
            }

            string[] file_paths = Directory.GetFiles(SCENE_SNO_CACHE_DIR);

            foreach (string sno_file in file_paths)
            {
                int scene_sno_id = int.Parse(Path.GetFileName(sno_file));
                m_SnoCache.Add(scene_sno_id, new SceneSnoNavData(scene_sno_id));
            }
        }

        private static string SCENE_SNO_CACHE_DIR = "sno_cache/";
        private static bool USE_SNO_CACHE = true;

        private Object m_FetchNavDataLock = new Object();
        private Object m_FetchDangerRegionsLock = new Object();
        private ReaderWriterLockSlim ProcessedScenesLock = new ReaderWriterLockSlim();
        private ReaderWriterLockSlim D3InputLock = new ReaderWriterLockSlim();

        private Dictionary<int, SceneSnoNavData> m_SnoCache = new Dictionary<int, SceneSnoNavData>();
        private HashSet<SceneData.uid> m_ProcessedSceneId = new HashSet<SceneData.uid>(); // @ProcessedScenesLock
        private List<int> m_AllowedAreasSnoId = new List<int>(); //@D3InputLock
        private List<int> m_AllowedGridCellsId = new List<int>(); //@D3InputLock
        private System.Timers.Timer m_FetchNavDataTimer = new System.Timers.Timer();
        private System.Timers.Timer m_FetchDangerRegionsTimer = new System.Timers.Timer();
        private Engine m_Engine;
        private int m_LastFrame;
        private LocalData m_LocalData;
        private ObjectManager m_ObjectManager;
        private bool m_IsLocalActorReady = false;
    }
}
