using System.Collections.Generic;
using Il2Cpp;
using Il2CppTLD.Gear;
using Il2CppTLD.Interactions;
using UnityEngine;

namespace SteamRailway
{
    /// Hard mode: a broken bridge is repaired in stages at a board beside the gap's nearest end; every crafted
    /// stage item ("GEAR_SRBridgeStageN") is one working hour, and a finished stage shows its scaffold.
    internal static class BridgeSite
    {
        sealed class Stage
        {
            public string Turkish, English;
            public int Hours;
        }

        /// The working hours of each stage; the materials of one hour are in the stage's blueprint.
        static readonly Stage[] Stages =
        {
            new Stage { Turkish = "Enkazı temizle", English = "Clear the debris", Hours = 3 },
            new Stage { Turkish = "Ayak takviyesi", English = "Reinforce the footings", Hours = 4 },
            new Stage { Turkish = "Ana kirişler", English = "Set the main girders", Hours = 6 },
            new Stage { Turkish = "Traversler ve döşeme", English = "Lay the sleepers and deck", Hours = 5 },
            new Stage { Turkish = "Raylar", English = "Lay the rails", Hours = 4 },
        };

        public const string StageGearPrefix = "GEAR_SRBridgeStage";
        /// The crafted stage items' gear names, built once (the per-second check must not build strings).
        static readonly string[] StageGearNames = BuildStageGearNames();

        static string[] BuildStageGearNames()
        {
            var names = new string[Stages.Length];
            for (int index = 0; index < names.Length; index++) names[index] = StageGearPrefix + (index + 1);
            return names;
        }
        const string ZoneName = "SR_BridgeSiteZone";
        const string WorksName = "SR_BridgeWorks";
        const float ApproachInMeters = 60f;
        const float SiteOffsetFromTrackInMeters = 2.8f;
        const float RefreshIntervalInSeconds = 0.5f;
        const float HoldTimeInSeconds = 0.3f;
        // scaffold (metres; the line's points are the rail-top centre)
        const float TieSpacingInMeters = 0.85f;
        const float SegmentLengthInMeters = 1.5f;
        const float GirderSideOffsetInMeters = 0.85f;
        const float GirderDropInMeters = 0.52f;
        const float RailGaugeHalfInMeters = 0.72f;
        const float BentSpacingInMeters = 8f;
        const float BentWidthInMeters = 1.2f;
        const float MaximumBentDepthInMeters = 45f;
        const float TextureTileInMeters = 1.6f;
        const float SiteOutwardOffsetInMeters = 2f;
        const float SiteGroundProbeHeightInMeters = 3f;
        const float SiteGroundProbeDistanceInMeters = 8f;
        const float MinimumFacingSquaredDistance = 0.01f;
        const float MinimumDirectionSquaredLength = 0.0001f;
        const int FirstBentStage = 2;
        const int GirderStage = 3;
        const int TieStage = 4;
        const int MinimumBentCount = 2;
        // the notice board
        const float PostLateralInMeters = 0.72f;
        const float PostCenterHeightInMeters = 1.0f;
        const float PostLeanInDegrees = 1.5f;
        const float PostThicknessInMeters = 0.13f;
        const float PostHeightInMeters = 2.0f;
        const float StrutCenterHeightInMeters = 0.2f;
        const float StrutForwardInMeters = 0.25f;
        const float StrutTiltInDegrees = -35f;
        const float StrutThicknessInMeters = 0.08f;
        const float StrutLengthInMeters = 0.7f;
        const int PlankCount = 4;
        const float PlankSpacingInMeters = 0.21f;
        const float PlankDepthInMeters = -0.08f;
        const float PlankTiltEvenInDegrees = 0.6f;
        const float PlankTiltOddInDegrees = -0.5f;
        const float PlankWidthInMeters = 1.62f;
        const float PlankHeightInMeters = 0.19f;
        const float PlankThicknessInMeters = 0.045f;
        const float LedgeHeightInMeters = 0.9f;
        const float LedgeDepthInMeters = -0.12f;
        const float LedgeWidthInMeters = 1.5f;
        const float LedgeHeightSizeInMeters = 0.07f;
        const float LedgeThicknessInMeters = 0.14f;
        const float RoofHeightInMeters = 2.02f;
        const float RoofDepthInMeters = 0.02f;
        const float RoofTiltInDegrees = 28f;
        const float RoofWidthInMeters = 1.9f;
        const float RoofThicknessInMeters = 0.045f;
        const float RoofDepthSizeInMeters = 0.5f;
        const float PlanSheetLateralInMeters = -0.25f;
        const float PlanSheetHeightInMeters = 1.35f;
        const float PlanSheetDepthInMeters = -0.108f;
        const float PlanSheetTiltInDegrees = 3f;
        const float PlanSheetWidthInMeters = 0.5f;
        const float PlanSheetHeightSizeInMeters = 0.36f;
        const float PlanSheetThicknessInMeters = 0.01f;
        const float ZoneHeightInMeters = 1.35f;
        const float ZoneDepthInMeters = -0.1f;
        const float ZoneWidthInMeters = 1.7f;
        const float ZoneHeightSizeInMeters = 1.0f;
        const float ZoneThicknessInMeters = 0.4f;
        const float WoodFallbackRed = 0.36f;
        const float WoodFallbackGreen = 0.30f;
        const float WoodFallbackBlue = 0.23f;
        const float SteelFallbackRed = 0.30f;
        const float SteelFallbackGreen = 0.24f;
        const float SteelFallbackBlue = 0.20f;
        // the scaffold
        const float GirderSectionWidthInMeters = 0.26f;
        const float GirderSectionHeightInMeters = 0.34f;
        const float FirstTieOffsetInMeters = 0.3f;
        const float TieDropInMeters = -0.25f;
        const float TieLengthInMeters = 2.6f;
        const float TieHeightInMeters = 0.15f;
        const float TieWidthInMeters = 0.26f;
        const float PieceOverlapInMeters = 0.04f;
        const float BentCapExtraDropInMeters = 0.32f;
        const float BentProbeDropInMeters = 0.3f;
        const float BentDefaultDepthInMeters = 1.5f;
        const float BentMinimumDepthInMeters = 1f;
        const float BentGroundSinkInMeters = 0.6f;
        const float BentPostThicknessInMeters = 0.3f;
        const float BentCapExtraWidthInMeters = 0.5f;
        const float BentCapHeightInMeters = 0.3f;
        const float BentCapDepthInMeters = 0.32f;
        const float BraceMaximumDropInMeters = 3f;
        const float BraceCentreExtraDropInMeters = 0.15f;
        const float BraceThicknessInMeters = 0.18f;
        // the game's own materials (found among the loaded ones; a plain tinted material when missing)
        const string WoodMaterialName = "GLB_WoodWallNatural_C04";
        const string MetalMaterialName = "GLB_MetalRusted_B01";

        static ObstacleData s_Obstacle;
        static GameObject s_Site;
        static GameObject s_Works;
        static WorkBench s_Bench;
        static int s_ShownStages = -1;
        static float s_NextRefreshTime;
        static Material s_WoodMaterial, s_SteelMaterial;
        static BuiltTrack s_Track;
        static float s_RangeStart, s_RangeEnd;
        static Vector3 s_SitePoint;
        static bool s_HasSpan;
        static bool s_MenuOpen;
        static readonly Dictionary<string, Mesh> s_Boxes = new Dictionary<string, Mesh>();

        /// A region is built or left: nothing of the previous one stays.
        public static void Clear()
        {
            s_Obstacle = null; s_Site = null; s_Works = null; s_Bench = null; s_ShownStages = -1; s_HasSpan = false;
            s_WoodMaterial = null; s_SteelMaterial = null; s_Track = null; s_MenuOpen = false; s_WorkSoundHolder = null; s_WorkSoundSource = null; s_CraftingPanel = null; s_ZoneInteraction = null; s_HoverStage = -1; s_HoverHours = -1;
        }

        static Panel_Crafting s_CraftingPanel;

        /// The game's crafting panel, looked up once.
        static Panel_Crafting CraftingPanel()
        {
            if (s_CraftingPanel == null) s_CraftingPanel = InterfaceManager.GetPanel<Panel_Crafting>();
            return s_CraftingPanel;
        }

        public static bool IsZone(GameObject candidate) => candidate != null && candidate.name == ZoneName;

        static int StageOf(ObstacleData obstacle) => RailwayManager.BridgeStage(obstacle.Identifier);

        static int HoursDone(ObstacleData obstacle) => Mathf.Max(0, RailwayManager.BridgeWork(obstacle.Identifier));

        // ------------------------------------------------------------------ the crafting menu (see Patches/BridgePatches)

        /// The board's crafting menu is open: only the stage under way is listed there (and stages nowhere else).
        public static bool MenuOpen => s_MenuOpen;

        /// The stage (0-based) a blueprint stands for, or -1 for any other blueprint.
        public static int StageOfBlueprint(BlueprintData blueprint)
        {
            if (blueprint == null || blueprint.m_CraftedResultGear == null) return -1;
            string name = blueprint.m_CraftedResultGear.name;
            if (!name.StartsWith(StageGearPrefix)) return -1;
            return int.TryParse(name.Substring(StageGearPrefix.Length), out int number) ? number - 1 : -1;
        }

        /// Whether the blueprint is the stage under way at the open board.
        public static bool IsCurrentStage(BlueprintData blueprint) =>
            s_MenuOpen && s_Obstacle != null && StageOfBlueprint(blueprint) == StageOf(s_Obstacle);

        /// Hours left in the stage under way (the most the quantity selector offers).
        public static int RemainingHours()
        {
            if (s_Obstacle == null) return 0;
            int stage = StageOf(s_Obstacle);
            return stage >= Stages.Length ? 0 : Mathf.Max(0, Stages[stage].Hours - HoursDone(s_Obstacle));
        }

        static void OpenMenu()
        {
            if (s_Bench == null) return;
            var panel = CraftingPanel();
            if (panel == null) return;
            // the game's list breaks on an empty list: the stage blueprints come from the item pack (missing until the
            // pack has been read at a game start)
            if (!StageItemsLoaded())
            {
                CabControls.Message(Localizer.Translate("Onarım planı yüklenemedi (eşya paketi okunmamış; oyunu yeniden başlat).", "The repair plan could not be loaded (the item pack was not read; restart the game)."));
                return;
            }
            s_MenuOpen = true;
            panel.EnableCraftingAtLocation(new CraftingLocationInterface(s_Bench.Pointer));
        }

        static bool? s_StageItemsLoaded;

        static bool StageItemsLoaded()
        {
            if (s_StageItemsLoaded.HasValue) return s_StageItemsLoaded.Value;
            bool loaded = false;
            try { loaded = UnityEngine.AddressableAssets.Addressables.LoadAssetAsync<GameObject>(StageGearPrefix + "1").WaitForCompletion() != null; }
            catch (System.Exception exception) { SteamRailwayMod.Debug($"Bridge stage items: {exception.Message}"); }
            s_StageItemsLoaded = loaded;
            return loaded;
        }

        /// The hold on the board completed: the repair menu opens.
        public static bool Perform()
        {
            if (s_Obstacle == null || !s_HasSpan || StageOf(s_Obstacle) >= Stages.Length) return false;
            OpenMenu();
            return true;
        }

        // ------------------------------------------------------------------ work sound

        const float WorkSoundVolume = 0.8f;
        const float WorkSoundMinimumDistanceInMeters = 4f;
        const float WorkSoundMaximumDistanceInMeters = 40f;
        static GameObject s_WorkSoundHolder;
        static AudioSource s_WorkSoundSource;

        /// The stage's working hours begin (the game's progress bar is running): the work sound loops until it ends.
        public static void StartWorkSound()
        {
            if (s_Site == null) return;
            if (s_WorkSoundHolder == null)
            {
                s_WorkSoundHolder = new GameObject("SR_BridgeWorkSound");
                s_WorkSoundSource = SynthSounds.Source(s_WorkSoundHolder, true, WorkSoundMinimumDistanceInMeters, WorkSoundMaximumDistanceInMeters);
                s_WorkSoundSource.clip = WorldSounds.BridgeWork;
                s_WorkSoundSource.volume = WorkSoundVolume;
            }
            s_WorkSoundHolder.transform.position = s_Site.transform.position + Vector3.up;
            s_WorkSoundSource.volume = WorkSoundVolume * GameVolume.Scale;
            if (!s_WorkSoundSource.isPlaying) s_WorkSoundSource.Play();
        }

        static void StopWorkSound()
        {
            if (s_WorkSoundSource != null && s_WorkSoundSource.isPlaying) s_WorkSoundSource.Stop();
        }

        // ------------------------------------------------------------------ every frame

        public static void Update()
        {
            if (!Obstacles.HardMode) return;
            if (s_WorkSoundSource != null && s_WorkSoundSource.isPlaying)
            {
                var craftingPanel = CraftingPanel();
                if (craftingPanel == null || !craftingPanel.IsAcceleratingTime()) StopWorkSound();
            }
            if (s_MenuOpen)
            {
                var panel = CraftingPanel();
                if (panel == null || !panel.IsEnabled()) s_MenuOpen = false;
                // the menu opened from the board hides the requirements column after it has been filled; show it again
                else if (IsCurrentStage(panel.SelectedBPI) && panel.m_RequirementContainer != null && !panel.m_RequirementContainer.gameObject.activeSelf)
                    panel.m_RequirementContainer.Enable(panel);
            }
            if (Time.time < s_NextRefreshTime) return;
            s_NextRefreshTime = Time.time + RefreshIntervalInSeconds;
            var player = GameManager.GetPlayerObject();
            if (player == null) return;
            var playerPosition = player.transform.position;
            var near = FindNearBridge(playerPosition);
            if (near != s_Obstacle)
            {
                DestroyWorks();
                DestroySite();
                s_Obstacle = near;
                s_ShownStages = -1;
                s_HasSpan = near != null && FindSpan(near, playerPosition);
                if (s_HasSpan) BuildSite();
            }
            if (s_Obstacle == null || !s_HasSpan) return;
            TakeCraftedHours();
            if (s_Obstacle == null) return;
            if (s_Works == null || s_ShownStages != StageOf(s_Obstacle)) ShowStages(StageOf(s_Obstacle));
            RefreshZoneText();
        }

        /// Turns the crafted stage "items" in the inventory into working hours of the stage under way.
        static void TakeCraftedHours()
        {
            var inventory = GameManager.GetInventoryComponent();
            if (inventory == null) return;
            for (int stageIndex = 0; stageIndex < Stages.Length; stageIndex++)
            {
                string gearName = StageGearNames[stageIndex];
                int crafted = inventory.GetNumGearWithName(gearName);
                if (crafted <= 0) continue;
                inventory.RemoveGearFromInventory(gearName, crafted);
                if (s_Obstacle == null || stageIndex != StageOf(s_Obstacle)) continue;
                AddHours(crafted);
                RefreshOpenMenu();
            }
        }

        /// The menu stays open after the work and lists the stage under way again with the hours left.
        static void RefreshOpenMenu()
        {
            if (!s_MenuOpen) return;
            var panel = InterfaceManager.GetPanel<Panel_Crafting>();
            if (panel == null || !panel.IsEnabled()) return;
            if (s_Obstacle == null) { panel.Enable(false); return; }
            panel.RefreshAvailableBlueprints();
            panel.ApplyFilter();
            panel.RefreshSelectedBlueprint(0);
        }

        static void AddHours(int hours)
        {
            int stageIndex = StageOf(s_Obstacle);
            var stage = Stages[stageIndex];
            int done = HoursDone(s_Obstacle) + hours;
            if (done < stage.Hours)
            {
                RailwayManager.SetBridgeWork(s_Obstacle.Identifier, done);
                return;
            }
            RailwayManager.SetBridgeWork(s_Obstacle.Identifier, 0);
            stageIndex++;
            RailwayManager.SetBridgeStage(s_Obstacle.Identifier, stageIndex);
            if (stageIndex >= Stages.Length)
            {
                CabControls.Message(Localizer.Translate("Köprü onarımı tamamlandı.", "The bridge repair is finished."));
                var finished = s_Obstacle;
                DestroyWorks();
                DestroySite();
                s_Obstacle = null;
                Obstacles.Clear(finished);
                return;
            }
            ShowStages(stageIndex);
        }

        static ObstacleData FindNearBridge(Vector3 playerPosition)
        {
            var obstacles = Obstacles.All;
            for (int index = 0; index < obstacles.Count; index++)
            {
                var obstacle = obstacles[index];
                if (Obstacles.KindOf(obstacle) != ObstacleKind.BrokenBridge || !Obstacles.IsHolding(obstacle)) continue;
                var center = Obstacles.CenterOf(obstacle);
                var flat = new Vector2(playerPosition.x - center.x, playerPosition.z - center.z);
                if (flat.magnitude <= obstacle.Radius + ApproachInMeters) return obstacle;
            }
            return null;
        }

        /// The gap on the line (path metres); the board stands beside the end nearer to the player.
        static bool FindSpan(ObstacleData obstacle, Vector3 playerPosition)
        {
            float longest = 0f;
            foreach (var track in RailwayManager.Tracks)
            {
                foreach (var range in Obstacles.UnrepairedBridgeRanges(track.Path))
                {
                    if (range.y - range.x <= longest) continue;
                    track.Path.Sample(range.x, out var start, out var startTangent);
                    track.Path.Sample(range.y, out var end, out var endTangent);
                    if (!Obstacles.Contains(obstacle, start.x, start.z) && !Obstacles.Contains(obstacle, end.x, end.z)) continue;
                    longest = range.y - range.x;
                    s_Track = track;
                    s_RangeStart = range.x;
                    s_RangeEnd = range.y;
                    bool startNearer = (start - playerPosition).sqrMagnitude <= (end - playerPosition).sqrMagnitude;
                    var tangent = (startNearer ? startTangent : endTangent).normalized;
                    var outward = startNearer ? -tangent : tangent;
                    var side = Vector3.Cross(Vector3.up, tangent).normalized;
                    s_SitePoint = (startNearer ? start : end) + outward * SiteOutwardOffsetInMeters + side * SiteOffsetFromTrackInMeters;
                    if (Physics.Raycast(s_SitePoint + Vector3.up * SiteGroundProbeHeightInMeters, Vector3.down, out var ground, SiteGroundProbeDistanceInMeters, ~0, QueryTriggerInteraction.Ignore))
                        s_SitePoint.y = ground.point.y;
                }
            }
            return longest > 0f;
        }

        // ------------------------------------------------------------------ the board

        /// A small roofed notice board of weathered planks; its face carries the hold that opens the menu.
        /// The board itself is a workbench, which the crafting menu needs.
        static void BuildSite()
        {
            DestroySite();
            var wood = WoodMaterial();
            s_Site = new GameObject("SR_BridgeSite");
            s_Site.transform.position = s_SitePoint;
            var middle = SamplePath((s_RangeStart + s_RangeEnd) * 0.5f, out _) - s_SitePoint;
            middle.y = 0f;
            if (middle.sqrMagnitude > MinimumFacingSquaredDistance) s_Site.transform.rotation = Quaternion.LookRotation(-middle);
            var parent = s_Site.transform;
            for (int side = -1; side <= 1; side += 2)
            {
                Piece(parent, new Vector3(side * PostLateralInMeters, PostCenterHeightInMeters, 0f), Quaternion.Euler(0f, 0f, side * PostLeanInDegrees), new Vector3(PostThicknessInMeters, PostHeightInMeters, PostThicknessInMeters), wood);
                Piece(parent, new Vector3(side * PostLateralInMeters, StrutCenterHeightInMeters, StrutForwardInMeters), Quaternion.Euler(StrutTiltInDegrees, 0f, 0f), new Vector3(StrutThicknessInMeters, StrutLengthInMeters, StrutThicknessInMeters), wood);
            }
            for (int plank = 0; plank < PlankCount; plank++)
                Piece(parent, new Vector3(0f, PostCenterHeightInMeters + plank * PlankSpacingInMeters, PlankDepthInMeters), Quaternion.Euler(0f, 0f, plank % 2 == 0 ? PlankTiltEvenInDegrees : PlankTiltOddInDegrees), new Vector3(PlankWidthInMeters, PlankHeightInMeters, PlankThicknessInMeters), wood);
            Piece(parent, new Vector3(0f, LedgeHeightInMeters, LedgeDepthInMeters), Quaternion.identity, new Vector3(LedgeWidthInMeters, LedgeHeightSizeInMeters, LedgeThicknessInMeters), wood);
            Piece(parent, new Vector3(0f, RoofHeightInMeters, RoofDepthInMeters), Quaternion.Euler(RoofTiltInDegrees, 0f, 0f), new Vector3(RoofWidthInMeters, RoofThicknessInMeters, RoofDepthSizeInMeters), wood);
            // the plan: a sheet of rusty tin nailed to the planks
            Piece(parent, new Vector3(PlanSheetLateralInMeters, PlanSheetHeightInMeters, PlanSheetDepthInMeters), Quaternion.Euler(0f, 0f, PlanSheetTiltInDegrees), new Vector3(PlanSheetWidthInMeters, PlanSheetHeightSizeInMeters, PlanSheetThicknessInMeters), SteelMaterial());
            s_Bench = s_Site.AddComponent<WorkBench>();
            s_Bench.m_CraftingAndRepairTimeModifier = 1f;
            s_Bench.m_CraftingAndRepairSkillModifier = 0f;
            var zone = Dynamite.BuildZone(ZoneName, Localizer.Translate("Onarım planı açılıyor...", "Opening the repair plan..."), HoldTimeInSeconds);
            zone.transform.SetParent(parent, false);
            zone.transform.localPosition = new Vector3(0f, ZoneHeightInMeters, ZoneDepthInMeters);
            var box = zone.GetComponent<BoxCollider>();
            if (box != null) box.size = new Vector3(ZoneWidthInMeters, ZoneHeightSizeInMeters, ZoneThicknessInMeters);
            zone.SetActive(true);
        }

        static void DestroySite()
        {
            if (s_Site != null) Object.Destroy(s_Site);
            s_Site = null;
            s_Bench = null;
            s_ZoneInteraction = null;
            s_HoverStage = -1;
        }

        static GameObject Zone() => s_Site != null ? s_Site.transform.Find(ZoneName)?.gameObject : null;

        static BaseInteraction s_ZoneInteraction;
        static int s_HoverStage = -1, s_HoverHours = -1;

        /// The board's hover text shows the stage and its hours; it is rebuilt only when either changes.
        static void RefreshZoneText()
        {
            var zone = Zone();
            if (zone == null || s_Obstacle == null) return;
            int stageIndex = StageOf(s_Obstacle);
            if (stageIndex >= Stages.Length) { zone.SetActive(false); return; }
            int hoursDone = HoursDone(s_Obstacle);
            if (s_ZoneInteraction == null) s_ZoneInteraction = zone.GetComponent<BaseInteraction>();
            if (s_ZoneInteraction == null || (stageIndex == s_HoverStage && hoursDone == s_HoverHours)) return;
            s_HoverStage = stageIndex;
            s_HoverHours = hoursDone;
            var stage = Stages[stageIndex];
            s_ZoneInteraction.HoverText = Localizer.Translate($"Köprü onarımı {stageIndex + 1}/{Stages.Length}: {stage.Turkish} ({hoursDone}/{stage.Hours} saat) – planı aç",
                                              $"Bridge repair {stageIndex + 1}/{Stages.Length}: {stage.English} ({hoursDone}/{stage.Hours} h) – open the plan");
        }

        // ------------------------------------------------------------------ materials and boxes

        static Material WoodMaterial() => s_WoodMaterial ??= GameMaterial(WoodMaterialName, new Color(WoodFallbackRed, WoodFallbackGreen, WoodFallbackBlue));

        static Material SteelMaterial() => s_SteelMaterial ??= GameMaterial(MetalMaterialName, new Color(SteelFallbackRed, SteelFallbackGreen, SteelFallbackBlue));

        /// One of the game's loaded materials by name (a world texture that tiles); a tinted gear-shader material otherwise.
        static Material GameMaterial(string name, Color fallback)
        {
            foreach (var material in Resources.FindObjectsOfTypeAll<Material>())
                if (material != null && material.name.StartsWith(name)) return material;
            SteamRailwayMod.Debug($"Bridge site: material {name} is not loaded here; a plain one is used.");
            var plain = Dynamite.GameShaderMaterial();
            if (plain != null) plain.color = fallback;
            return plain;
        }

        /// A box whose texture tiles by size (UVs in metres / TextureTileInMeters on every face), so long beams do not
        /// stretch the wood. Shared by size.
        static Mesh BoxMesh(Vector3 size)
        {
            string key = $"{size.x:F2}x{size.y:F2}x{size.z:F2}";
            if (s_Boxes.TryGetValue(key, out var cached) && cached != null) return cached;
            var half = size * 0.5f;
            var vertices = new List<Vector3>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            void Face(Vector3 origin, Vector3 widthEdge, Vector3 heightEdge)
            {
                int start = vertices.Count;
                float width = widthEdge.magnitude / TextureTileInMeters, height = heightEdge.magnitude / TextureTileInMeters;
                vertices.Add(origin); vertices.Add(origin + widthEdge); vertices.Add(origin + widthEdge + heightEdge); vertices.Add(origin + heightEdge);
                uvs.Add(new Vector2(0f, 0f)); uvs.Add(new Vector2(width, 0f)); uvs.Add(new Vector2(width, height)); uvs.Add(new Vector2(0f, height));
                triangles.Add(start); triangles.Add(start + 2); triangles.Add(start + 1);
                triangles.Add(start); triangles.Add(start + 3); triangles.Add(start + 2);
            }
            Face(new Vector3(-half.x, -half.y, -half.z), new Vector3(size.x, 0f, 0f), new Vector3(0f, size.y, 0f));   // -z
            Face(new Vector3(half.x, -half.y, half.z), new Vector3(-size.x, 0f, 0f), new Vector3(0f, size.y, 0f));    // +z
            Face(new Vector3(half.x, -half.y, -half.z), new Vector3(0f, 0f, size.z), new Vector3(0f, size.y, 0f));    // +x
            Face(new Vector3(-half.x, -half.y, half.z), new Vector3(0f, 0f, -size.z), new Vector3(0f, size.y, 0f));   // -x
            Face(new Vector3(-half.x, half.y, -half.z), new Vector3(0f, 0f, size.z), new Vector3(size.x, 0f, 0f));    // top
            Face(new Vector3(-half.x, -half.y, -half.z), new Vector3(size.x, 0f, 0f), new Vector3(0f, 0f, size.z));   // bottom
            var mesh = new Mesh();
            mesh.vertices = vertices.ToArray();
            mesh.uv = uvs.ToArray();
            mesh.triangles = triangles.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            s_Boxes[key] = mesh;
            return mesh;
        }

        /// A box part (local position and rotation under the parent) with a collider, so the player can walk on it.
        static GameObject Piece(Transform parent, Vector3 localPosition, Quaternion localRotation, Vector3 size, Material material)
        {
            var part = new GameObject("Piece");
            part.transform.SetParent(parent, false);
            part.transform.localPosition = localPosition;
            part.transform.localRotation = localRotation;
            part.AddComponent<MeshFilter>().sharedMesh = BoxMesh(size);
            var renderer = part.AddComponent<MeshRenderer>();
            if (material != null) renderer.sharedMaterial = material;
            part.AddComponent<BoxCollider>().size = size;
            return part;
        }

        // ------------------------------------------------------------------ the scaffold

        static Vector3 SamplePath(float pathMeters, out Vector3 tangent)
        {
            s_Track.Path.Sample(Mathf.Clamp(pathMeters, 0f, s_Track.Path.Length), out var point, out tangent);
            tangent = tangent.sqrMagnitude > MinimumDirectionSquaredLength ? tangent.normalized : Vector3.forward;
            return point;
        }

        static void DestroyWorks()
        {
            if (s_Works != null) Object.Destroy(s_Works);
            s_Works = null;
            s_ShownStages = -1;
        }

        /// Builds the scaffold of the finished stages along the line's own curve (all of it again; a few hundred boxes at most).
        static void ShowStages(int finished)
        {
            DestroyWorks();
            s_ShownStages = finished;
            if (finished <= 0 || finished >= Stages.Length || s_Track == null) return;
            s_Works = new GameObject(WorksName);
            var wood = WoodMaterial();
            var metal = SteelMaterial();
            float length = s_RangeEnd - s_RangeStart;
            if (finished >= FirstBentStage)
            {
                int bents = Mathf.Max(MinimumBentCount, Mathf.CeilToInt(length / BentSpacingInMeters) + 1);
                for (int bent = 0; bent < bents; bent++) BuildBent(s_RangeStart + length * bent / (bents - 1), wood);
            }
            if (finished >= GirderStage)
                for (int side = -1; side <= 1; side += 2)
                    AlongLine(side * GirderSideOffsetInMeters, -GirderDropInMeters, new Vector2(GirderSectionWidthInMeters, GirderSectionHeightInMeters), metal);
            if (finished >= TieStage)
                for (float distance = FirstTieOffsetInMeters; distance < length; distance += TieSpacingInMeters)
                {
                    var point = SamplePath(s_RangeStart + distance, out var tangent);
                    var rotation = Quaternion.LookRotation(tangent, Vector3.up);
                    Piece(s_Works.transform, point + rotation * new Vector3(0f, TieDropInMeters, 0f), rotation, new Vector3(TieLengthInMeters, TieHeightInMeters, TieWidthInMeters), wood);
                }
        }

        /// A beam (cross-section width x height) following the line at a side offset and height, in short straight pieces.
        static void AlongLine(float sideOffset, float heightOffset, Vector2 section, Material material)
        {
            float length = s_RangeEnd - s_RangeStart;
            int pieces = Mathf.Max(1, Mathf.CeilToInt(length / SegmentLengthInMeters));
            for (int piece = 0; piece < pieces; piece++)
            {
                float fromPathMeters = s_RangeStart + length * piece / pieces;
                float toPathMeters = s_RangeStart + length * (piece + 1) / pieces;
                var start = Offset(fromPathMeters, sideOffset, heightOffset);
                var end = Offset(toPathMeters, sideOffset, heightOffset);
                var along = end - start;
                if (along.sqrMagnitude < MinimumDirectionSquaredLength) continue;
                Piece(s_Works.transform, (start + end) * 0.5f, Quaternion.LookRotation(along, Vector3.up), new Vector3(section.x, section.y, along.magnitude + PieceOverlapInMeters), material);
            }
        }

        static Vector3 Offset(float pathMeters, float sideOffset, float heightOffset)
        {
            var point = SamplePath(pathMeters, out var tangent);
            var side = Vector3.Cross(Vector3.up, tangent).normalized;
            return point + side * sideOffset + Vector3.up * heightOffset;
        }

        /// A timber trestle across the line: two posts down to the ground, a cap beam and a diagonal brace.
        static void BuildBent(float pathMeters, Material wood)
        {
            var top = SamplePath(pathMeters, out var tangent) + Vector3.down * (GirderDropInMeters + BentCapExtraDropInMeters);
            var flat = new Vector3(tangent.x, 0f, tangent.z);
            var rotation = Quaternion.LookRotation(flat.sqrMagnitude > MinimumDirectionSquaredLength ? flat : Vector3.forward, Vector3.up);
            var side = rotation * Vector3.right;
            float deepest = 1f;
            for (int post = -1; post <= 1; post += 2)
            {
                var foot = top + side * (BentWidthInMeters * post);
                float depth = BentDefaultDepthInMeters;
                if (Physics.Raycast(foot + Vector3.down * BentProbeDropInMeters, Vector3.down, out var ground, MaximumBentDepthInMeters, ~0, QueryTriggerInteraction.Ignore))
                    depth = Mathf.Max(BentMinimumDepthInMeters, ground.distance + BentGroundSinkInMeters);
                deepest = Mathf.Max(deepest, depth);
                Piece(s_Works.transform, foot + Vector3.down * (depth * 0.5f), rotation, new Vector3(BentPostThicknessInMeters, depth, BentPostThicknessInMeters), wood);
            }
            Piece(s_Works.transform, top, rotation, new Vector3(BentWidthInMeters * 2f + BentCapExtraWidthInMeters, BentCapHeightInMeters, BentCapDepthInMeters), wood);
            float braceDrop = Mathf.Min(deepest * 0.5f, BraceMaximumDropInMeters);
            float span = BentWidthInMeters * 2f;
            var braceCentre = top + Vector3.down * (braceDrop * 0.5f + BraceCentreExtraDropInMeters);
            float braceLength = Mathf.Sqrt(braceDrop * braceDrop + span * span);
            var braceRotation = rotation * Quaternion.Euler(0f, 0f, Mathf.Atan2(braceDrop, span) * Mathf.Rad2Deg);
            Piece(s_Works.transform, braceCentre, braceRotation, new Vector3(braceLength, BraceThicknessInMeters, BraceThicknessInMeters), wood);
        }
    }
}
