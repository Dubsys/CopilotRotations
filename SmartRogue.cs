using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Styx;
using Styx.Combat.CombatRoutine; 
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

namespace SmartRogue
{
    public enum PauseKeyMode { None, LeftControl, LeftShift, LeftAlt }

    // ------------------------------------------------------
    // SETTINGS CONFIGURATION CLASS
    // ------------------------------------------------------
    public class RogueSettings : Styx.Helpers.Settings
    {
        private static RogueSettings _instance;
        public static RogueSettings Instance { get { return _instance ?? (_instance = new RogueSettings()); } }

        public RogueSettings() : base(Styx.Helpers.Logging.ApplicationPath + "\\Settings\\SmartRogueSettings_" + StyxWoW.Me.Name + ".xml") { Load(); }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("1. Melee Latency Optimizer")]
        [DisplayName("Enable GCD Queueing")]
        public bool EnableSpellQueueing { get; set; }

        [Setting, Styx.Helpers.DefaultValue(300)]
        [Category("1. Melee Latency Optimizer")]
        [DisplayName("Queue Window (ms)")]
        public int SpellQueueWindow { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Utility")]
        [DisplayName("Auto Stealth")]
        public bool AutoStealth { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Utility")]
        [DisplayName("Auto Poisons")]
        public bool AutoPoisons { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Utility")]
        [DisplayName("Tricks of the Trade")]
        public bool UseTricks { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Interrupt (Kick)")]
        public bool AutoInterrupt { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Evasion (Survival)")]
        public bool AutoEvasion { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Spec Cooldowns")]
        public bool AutoCombatCooldowns { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Use Saronite Bombs")]
        public bool UseBombs { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Maintain Rupture (Combat)")]
        public bool UseRupture { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Use Fan of Knives")]
        public bool UseFanOfKnives { get; set; }

        [Setting, Styx.Helpers.DefaultValue(3)]
        [Category("3. Combat Options")]
        [DisplayName("Fan of Knives Count")]
        public int FoKCount { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Use Engineering Gloves")]
        public bool UseEngiGloves { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Use On-Use Trinkets")]
        public bool UseTrinkets { get; set; }

        [Setting, Styx.Helpers.DefaultValue(PauseKeyMode.LeftControl)]
        [Category("4. Hotkeys & Overrides")]
        [DisplayName("Pause Modifier Key")]
        public PauseKeyMode PauseModifierKey { get; set; }
    }

    // ------------------------------------------------------
    // MAIN COMBAT ROUTINE
    // ------------------------------------------------------
    public class RogueCR : CombatRoutine
    {
        public override string Name { get { return "Smart Rogue Multi-Spec (Max Perf)"; } }
        public override WoWClass Class { get { return WoWClass.Rogue; } }
        public override bool NeedRest { get { return StyxWoW.Me.HealthPercent < 40; } }
        public override double? PullDistance { get { return 5.0; } }

        public override bool WantButton { get { return true; } }
        public override void OnButtonPress()
        {
            Form configForm = new Form { Text = "Smart Rogue Settings", Width = 420, Height = 640, StartPosition = FormStartPosition.CenterScreen };
            PropertyGrid grid = new PropertyGrid { SelectedObject = RogueSettings.Instance, Dock = DockStyle.Fill };
            configForm.Controls.Add(grid);
            configForm.FormClosing += (s, e) => RogueSettings.Instance.Save();
            configForm.ShowDialog();
        }

        // --- HARDWARE KEY POLLING ---
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LCONTROL = 0xA2;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_LMENU = 0xA4;

        private bool IsPauseModifierPressed()
        {
            switch (RogueSettings.Instance.PauseModifierKey)
            {
                case PauseKeyMode.LeftControl: return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
                case PauseKeyMode.LeftShift: return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
                case PauseKeyMode.LeftAlt: return (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
                default: return false;
            }
        }

        // --- OPTIMIZED CACHE & TIMER VARIABLES ---
        private DateTime _lastEnvironmentUpdate = DateTime.MinValue;
        private DateTime _lastItemUse = DateTime.MinValue;
        private DateTime _lastHfbCast = DateTime.MinValue; 
        private List<WoWPlayer> _cachedGroup = new List<WoWPlayer>();
        private WoWPlayer _cachedTank = null;
        private int _cachedAttackerCount = 0;
        private bool _isInParty = false;
        private bool _isAssassination = false;

        private Composite _combatBehavior;
        public override Composite CombatBehavior { get { return _combatBehavior ?? (_combatBehavior = CreateCombatBehavior()); } }

        private Composite _pullBehavior;
        public override Composite PullBehavior { get { return _pullBehavior ?? (_pullBehavior = CreatePullBehavior()); } }

        private Composite _restBehavior;
        public override Composite RestBehavior { get { return _restBehavior ?? (_restBehavior = CreateRestBehavior()); } }

        // ------------------------------------------------------
        // RESTING & PRE-COMBAT
        // ------------------------------------------------------
        private Composite CreateRestBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),

                new Decorator(ret => StyxWoW.Me.HasAura("Food") || StyxWoW.Me.HasAura("Drink"),
                    new Action(ret => RunStatus.Success)),

                // Auto Poisons Out of Combat
                new Decorator(ret => RogueSettings.Instance.AutoPoisons && !StyxWoW.Me.IsMoving && !StyxWoW.Me.IsCasting,
                    new PrioritySelector(
                        ApplyPoison("Instant Poison", 16), // Main Hand
                        ApplyPoison("Deadly Poison", 17)   // Off Hand
                    )),

                new Decorator(ret => RogueSettings.Instance.AutoStealth && !StyxWoW.Me.HasAura("Stealth") && SpellManager.CanCast("Stealth"),
                    CastSpell("Stealth"))
            );
        }

        // ------------------------------------------------------
        // PULLING (PvE Stealth Opener Logic)
        // ------------------------------------------------------
        private Composite CreatePullBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),

                new Decorator(ret => RogueSettings.Instance.AutoStealth && !StyxWoW.Me.HasAura("Stealth") && SpellManager.CanCast("Stealth"),
                    CastSpell("Stealth")),

                // Auto-Facing Edge Case Fix
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget),
                    new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),

                // PvE Openers using Dynamic Melee Bounding Box
                new Decorator(ret => StyxWoW.Me.HasAura("Stealth") && StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.IsWithinMeleeRangeOf(StyxWoW.Me.CurrentTarget),
                    new PrioritySelector(
                        CastSpell("Garrote", ret => !TargetHasAnyBleed()),
                        CastSpell("Ambush")
                    )),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.IsWithinMeleeRangeOf(StyxWoW.Me.CurrentTarget),
                    CastSpell(_isAssassination ? "Mutilate" : "Sinister Strike"))
            );
        }

        // ------------------------------------------------------
        // COMBAT
        // ------------------------------------------------------
        private Composite CreateCombatBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),
                
                // Auto-Facing Edge Case Fix
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget),
                    new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),
                
                // 1. SURVIVAL
                new Decorator(ret => RogueSettings.Instance.AutoEvasion && (StyxWoW.Me.HealthPercent < 40 || _cachedAttackerCount >= 2) && SpellManager.CanCast("Evasion"),
                    CastSpell("Evasion")),

                // 2. INTERRUPTS
                new Decorator(ret => RogueSettings.Instance.AutoInterrupt && StyxWoW.Me.CurrentTarget != null 
                                  && (StyxWoW.Me.CurrentTarget.IsCasting || StyxWoW.Me.CurrentTarget.IsChanneling) 
                                  && StyxWoW.Me.CurrentTarget.CanInterruptCurrentSpellCast 
                                  && SpellManager.CanCast("Kick"),
                    CastSpell("Kick")),

                // 3. UTILITY & OFF-GCD ITEMS
                new Decorator(ret => RogueSettings.Instance.UseTricks && _isInParty && _cachedTank != null && !StyxWoW.Me.HasAura("Tricks of the Trade") && SpellManager.CanCast("Tricks of the Trade"),
                    new Action(ret => 
                    {
                        Logging.Write("Casting Tricks of the Trade on " + _cachedTank.Name);
                        SpellManager.Cast("Tricks of the Trade", _cachedTank);
                        return RunStatus.Success;
                    })),

                new Decorator(ret => RogueSettings.Instance.UseBombs && StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.DistanceSqr <= 900 && StyxWoW.Me.CurrentTarget.InLineOfSight && CanUseSaroniteBomb(),
                    new Action(ret =>
                    {
                        var bomb = StyxWoW.Me.BagItems.FirstOrDefault(i => i.Entry == 41119);
                        if (bomb != null)
                        {
                            bomb.UseContainerItem();
                            SpellManager.ClickRemoteLocation(StyxWoW.Me.CurrentTarget.Location);
                            Logging.Write("[SmartRogue] Threw Saronite Bomb!");
                        }
                        return RunStatus.Failure; 
                    })),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && IsBossOrElite(StyxWoW.Me.CurrentTarget) && CanUseItems(),
                    new Action(ret =>
                    {
                        if (RogueSettings.Instance.UseEngiGloves && StyxWoW.Me.Inventory.Equipped.Hands != null && StyxWoW.Me.Inventory.Equipped.Hands.CooldownTimeLeft.TotalMilliseconds <= 0)
                        {
                            StyxWoW.Me.Inventory.Equipped.Hands.Use();
                            Logging.Write("[SmartRogue] Popped Engineering Gloves!");
                        }
                        if (RogueSettings.Instance.UseTrinkets)
                        {
                            if (StyxWoW.Me.Inventory.Equipped.Trinket1 != null && StyxWoW.Me.Inventory.Equipped.Trinket1.CooldownTimeLeft.TotalMilliseconds <= 0)
                            {
                                StyxWoW.Me.Inventory.Equipped.Trinket1.Use();
                            }
                            if (StyxWoW.Me.Inventory.Equipped.Trinket2 != null && StyxWoW.Me.Inventory.Equipped.Trinket2.CooldownTimeLeft.TotalMilliseconds <= 0)
                            {
                                StyxWoW.Me.Inventory.Equipped.Trinket2.Use();
                            }
                        }
                        _lastItemUse = DateTime.Now;
                        return RunStatus.Failure;
                    })),

                // 4. FAN OF KNIVES (AoE Overrides Single Target)
                new Decorator(ret => RogueSettings.Instance.UseFanOfKnives && _cachedAttackerCount >= RogueSettings.Instance.FoKCount && SpellManager.CanCast("Fan of Knives"),
                    CastSpell("Fan of Knives")),

                // 5. BRANCH TO SPEC ROTATION (Dynamic Bounding Box Melee Range)
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.IsWithinMeleeRangeOf(StyxWoW.Me.CurrentTarget),
                    new PrioritySelector(
                        new Decorator(ret => _isAssassination, CreateAssassinationRotation()),
                        CreateCombatRotation()
                    ))
            );
        }

        // ------------------------------------------------------
        // ASSASSINATION ROTATION
        // ------------------------------------------------------
        private Composite CreateAssassinationRotation()
        {
            return new PrioritySelector(
                // Force a low-CP Rupture if HfB is dropping and no bleed exists to refresh it
                new Decorator(ret => GetAuraTimeLeft("Hunger For Blood") < 3000 && !TargetHasAnyBleed() && StyxWoW.Me.ComboPoints >= 1,
                    CastSpell("Rupture")),

                new Decorator(ret => TargetHasAnyBleed() && (DateTime.Now - _lastHfbCast).TotalSeconds > 55,
                    new Action(ret =>
                    {
                        if (SpellManager.CanCast("Hunger For Blood"))
                        {
                            SpellManager.Cast("Hunger For Blood");
                            Logging.Write("[SmartRogue] Casting: Hunger For Blood");
                            _lastHfbCast = DateTime.Now;
                            return RunStatus.Success;
                        }
                        return RunStatus.Failure;
                    })),

                new Decorator(ret => RogueSettings.Instance.AutoCombatCooldowns && StyxWoW.Me.ComboPoints >= 4 && IsBossOrElite(StyxWoW.Me.CurrentTarget) && SpellManager.CanCast("Cold Blood"),
                    new Action(ret =>
                    {
                        SpellManager.Cast("Cold Blood");
                        Logging.Write("[SmartRogue] Casting: Cold Blood");
                        return RunStatus.Failure; 
                    })),

                new Decorator(ret => StyxWoW.Me.ComboPoints > 0 && (!StyxWoW.Me.HasAura("Slice and Dice") || GetAuraTimeLeft("Slice and Dice") < 3000),
                    CastSpell("Slice and Dice")),

                // Energy Pooling Envenom Logic (Don't cast until 65 energy unless necessary)
                new Decorator(ret => StyxWoW.Me.ComboPoints >= 4 && (StyxWoW.Me.EnergyPercent >= 65 || GetAuraTimeLeft("Slice and Dice") < 3000 || StyxWoW.Me.CurrentTarget.HealthPercent < 20),
                    CastSpell("Envenom")),

                CastSpell("Mutilate")
            );
        }

        // ------------------------------------------------------
        // COMBAT ROTATION
        // ------------------------------------------------------
        private Composite CreateCombatRotation()
        {
            return new PrioritySelector(
                // Energy Gated Cooldowns (Prevents energy over-cap)
                new Decorator(ret => RogueSettings.Instance.AutoCombatCooldowns && IsBossOrElite(StyxWoW.Me.CurrentTarget),
                    new PrioritySelector(
                        new Decorator(ret => StyxWoW.Me.EnergyPercent >= 50 && _cachedAttackerCount > 1, CastSpell("Blade Flurry")),
                        new Decorator(ret => StyxWoW.Me.EnergyPercent <= 35, CastSpell("Killing Spree")),
                        new Decorator(ret => StyxWoW.Me.EnergyPercent <= 40, CastSpell("Adrenaline Rush"))
                    )),

                new Decorator(ret => StyxWoW.Me.ComboPoints > 0 && (!StyxWoW.Me.HasAura("Slice and Dice") || GetAuraTimeLeft("Slice and Dice") < 3000),
                    CastSpell("Slice and Dice")),

                // Finisher Priority
                new Decorator(ret => StyxWoW.Me.ComboPoints >= 4 || (StyxWoW.Me.ComboPoints >= 1 && !StyxWoW.Me.HasAura("Slice and Dice")),
                    new PrioritySelector(
                        new Decorator(ret => RogueSettings.Instance.UseRupture && StyxWoW.Me.ComboPoints == 5 && !StyxWoW.Me.CurrentTarget.HasAura("Rupture") && StyxWoW.Me.CurrentTarget.HealthPercent > 20,
                            CastSpell("Rupture")),
                        new Decorator(ret => StyxWoW.Me.ComboPoints >= 4, CastSpell("Eviscerate"))
                    )),

                CastSpell("Sinister Strike")
            );
        }

        // ------------------------------------------------------
        // THE GOD-LOOP (Pre-Filtered Native C# Scanner)
        // ------------------------------------------------------
        private void UpdateEnvironment()
        {
            if ((DateTime.Now - _lastEnvironmentUpdate).TotalMilliseconds < 200) return;

            _isAssassination = SpellManager.HasSpell("Mutilate");

            _cachedGroup.Clear();
            _cachedGroup.Add(StyxWoW.Me);
            _cachedAttackerCount = 0;
            _cachedTank = null;
            
            _isInParty = StyxWoW.Me.GroupInfo.IsInParty || StyxWoW.Me.GroupInfo.IsInRaid;

            foreach (WoWPlayer p in ObjectManager.GetObjectsOfType<WoWPlayer>())
            {
                if (p == null || !p.IsValid || !p.IsAlive || p.IsMe || !p.IsInMyPartyOrRaid) continue;

                if (p.DistanceSqr < 1600) 
                {
                    _cachedGroup.Add(p);
                    if (_cachedTank == null || p.MaxHealth > _cachedTank.MaxHealth)
                    {
                        _cachedTank = p; 
                    }
                }
            }

            foreach (WoWUnit u in ObjectManager.GetObjectsOfType<WoWUnit>())
            {
                if (u == null || !u.IsValid || !u.IsHostile || !u.IsAlive) continue;

                double distSqr = u.DistanceSqr;
                if (distSqr >= 100) continue; 

                if (u.IsTargetingMeOrPet)
                {
                    _cachedAttackerCount++;
                }
                else if (u.CurrentTargetGuid != 0 && _isInParty)
                {
                    if (_cachedGroup.Any(g => g.Guid == u.CurrentTargetGuid))
                    {
                        _cachedAttackerCount++;
                    }
                }
            }

            _lastEnvironmentUpdate = DateTime.Now;
        }

        // ------------------------------------------------------
        // NATIVE CACHED HELPERS & MELEE GCD QUEUE ENGINE
        // ------------------------------------------------------
        private bool TargetHasAnyBleed()
        {
            if (StyxWoW.Me.CurrentTarget == null) return false;
            string[] bleeds = { "Rupture", "Garrote", "Deep Wounds", "Rend", "Pounce Bleed", "Lacerate", "Tear", "Savage Rend" };
            return bleeds.Any(bleed => StyxWoW.Me.CurrentTarget.HasAura(bleed));
        }

        private Composite ApplyPoison(string poisonName, int inventorySlot)
        {
            return new Decorator(ret => NeedsPoison(inventorySlot),
                new Action(ret =>
                {
                    var item = StyxWoW.Me.BagItems.FirstOrDefault(i => i.Name.Contains(poisonName));
                    if (item != null)
                    {
                        item.UseContainerItem();
                        Lua.DoString($"UseInventoryItem({inventorySlot})"); 
                        Logging.Write($"[SmartRogue] Applying {poisonName} to slot {inventorySlot}");
                        return RunStatus.Success;
                    }
                    return RunStatus.Failure;
                })
            );
        }

        private bool NeedsPoison(int inventorySlot)
        {
            WoWItem item = inventorySlot == 16 ? StyxWoW.Me.Inventory.Equipped.MainHand : StyxWoW.Me.Inventory.Equipped.OffHand;
            return item != null && item.TemporaryEnchantment == null;
        }

        private bool CanUseSaroniteBomb()
        {
            var bomb = StyxWoW.Me.BagItems.FirstOrDefault(i => i.Entry == 41119);
            return bomb != null && bomb.CooldownTimeLeft.TotalMilliseconds <= 0;
        }

        private bool CanUseItems()
        {
            if (!RogueSettings.Instance.UseEngiGloves && !RogueSettings.Instance.UseTrinkets) return false;
            if ((DateTime.Now - _lastItemUse).TotalSeconds < 3) return false;
            return true;
        }

        private bool IsBossOrElite(WoWUnit u)
        {
            return u != null && (u.IsBoss || u.Elite || u.MaxHealth > (StyxWoW.Me.MaxHealth * 3) || u.IsPlayer);
        }

        private Composite CastSpell(string spellName, CanRunDecoratorDelegate extraCondition = null)
        {
            return new Decorator(ret => CustomCanCast(spellName) && (extraCondition == null || extraCondition(ret)),
                new Action(ret =>
                {
                    Logging.Write(string.Format("Casting: {0}", spellName));
                    SpellManager.Cast(spellName);
                    return RunStatus.Success;
                }));
        }

        private bool CustomCanCast(string spellName)
        {
            if (!SpellManager.HasSpell(spellName)) return false;

            if (SpellManager.CanCast(spellName)) return true;

            if (RogueSettings.Instance.EnableSpellQueueing)
            {
                if (SpellManager.Spells.ContainsKey(spellName))
                {
                    var cd = SpellManager.Spells[spellName].CooldownTimeLeft.TotalMilliseconds;
                    if (cd > 0 && cd <= RogueSettings.Instance.SpellQueueWindow)
                    {
                        return true; 
                    }
                }
            }
            return false;
        }

        private int GetAuraTimeLeft(string auraName)
        {
            if (!StyxWoW.Me.HasAura(auraName)) return 0;
            
            foreach (WoWAura aura in StyxWoW.Me.ActiveAuras.Values)
            {
                if (aura.Name == auraName)
                {
                    return (int)aura.TimeLeft.TotalMilliseconds;
                }
            }
            return 0;
        }
    }
}