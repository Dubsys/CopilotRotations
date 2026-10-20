using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using Styx;
using Styx.Combat.CombatRoutine; 
using Styx.Helpers;
using Styx.Logic.Combat;
using Styx.Logic.Pathing;
using Styx.WoWInternals;
using Styx.WoWInternals.WoWObjects;
using TreeSharp;
using Action = TreeSharp.Action;

namespace SmartBMHunter
{
    public enum PauseKeyMode { None, LeftControl, LeftShift, LeftAlt }

    // ------------------------------------------------------
    // SETTINGS CONFIGURATION CLASS
    // ------------------------------------------------------
    public class HunterSettings : Styx.Helpers.Settings
    {
        private static HunterSettings _instance;
        public static HunterSettings Instance { get { return _instance ?? (_instance = new HunterSettings()); } }

        public HunterSettings() : base(Styx.Helpers.Logging.ApplicationPath + "\\Settings\\SmartBMHunter_" + StyxWoW.Me.Name + ".xml") { Load(); }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("1. Latency Optimizer")]
        [DisplayName("Enable Cast Queueing")]
        public bool EnableSpellQueueing { get; set; }

        [Setting, Styx.Helpers.DefaultValue(300)]
        [Category("1. Latency Optimizer")]
        [DisplayName("Queue Window (ms)")]
        public int SpellQueueWindow { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Pet Management")]
        [DisplayName("Auto Pet Triage")]
        [Description("Automatically Revive, Call, and Mend your pet.")]
        public bool AutoPetTriage { get; set; }

        [Setting, Styx.Helpers.DefaultValue(60)]
        [Category("2. Pet Management")]
        [DisplayName("Mend Pet Threshold (%)")]
        public int MendPetPercent { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Mana & Aspects")]
        [DisplayName("Auto Aspect Switching")]
        [Description("Dynamically swap between Viper and Hawk based on mana thresholds.")]
        public bool AutoAspects { get; set; }

        [Setting, Styx.Helpers.DefaultValue(20)]
        [Category("3. Mana & Aspects")]
        [DisplayName("Viper Threshold (%)")]
        public int ViperManaPercent { get; set; }

        [Setting, Styx.Helpers.DefaultValue(80)]
        [Category("3. Mana & Aspects")]
        [DisplayName("Hawk Threshold (%)")]
        public int HawkManaPercent { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("4. Combat Options")]
        [DisplayName("Auto Bestial Wrath")]
        public bool AutoBestialWrath { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("5. Movement & Kiting")]
        [DisplayName("Enable Kiting")]
        [Description("Automatically stutter-step and kite mobs that get too close.")]
        public bool EnableKiting { get; set; }

        [Setting, Styx.Helpers.DefaultValue(12)]
        [Category("5. Movement & Kiting")]
        [DisplayName("Kite Threshold (Yards)")]
        [Description("If the mob crosses this distance, the kiting engine activates.")]
        public int KiteDistance { get; set; }

        [Setting, Styx.Helpers.DefaultValue(PauseKeyMode.LeftControl)]
        [Category("6. Hotkeys & Overrides")]
        [DisplayName("Pause Modifier Key")]
        public PauseKeyMode PauseModifierKey { get; set; }
    }

    // ------------------------------------------------------
    // MAIN COMBAT ROUTINE
    // ------------------------------------------------------
    public class BMHunterCR : CombatRoutine
    {
        public override string Name { get { return "Smart BM Leveling (Stutter-Step)"; } }
        public override WoWClass Class { get { return WoWClass.Hunter; } }
        public override bool NeedRest { get { return StyxWoW.Me.HealthPercent < 40 || (StyxWoW.Me.ManaPercent < 20 && !StyxWoW.Me.HasAura("Aspect of the Viper")); } }
        public override double? PullDistance { get { return 30.0; } }

        public override bool WantButton { get { return true; } }
        public override void OnButtonPress()
        {
            Form configForm = new Form { Text = "Beast Mastery Settings", Width = 420, Height = 640, StartPosition = FormStartPosition.CenterScreen };
            PropertyGrid grid = new PropertyGrid { SelectedObject = HunterSettings.Instance, Dock = DockStyle.Fill };
            configForm.Controls.Add(grid);
            configForm.FormClosing += (s, e) => HunterSettings.Instance.Save();
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
            switch (HunterSettings.Instance.PauseModifierKey)
            {
                case PauseKeyMode.LeftControl: return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
                case PauseKeyMode.LeftShift: return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
                case PauseKeyMode.LeftAlt: return (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
                default: return false;
            }
        }

        // --- CACHE & KITE TIMERS ---
        private DateTime _lastEnvironmentUpdate = DateTime.MinValue;
        private DateTime _kitePhaseTimer = DateTime.Now;
        private bool _isStoppingToShoot = false;
        private int _cachedAttackerCount = 0;

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

                CreatePetManagement(),
                CreateAspectManagement()
            );
        }

        // ------------------------------------------------------
        // PULLING
        // ------------------------------------------------------
        private Composite CreatePullBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget),
                    new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),

                CreatePetManagement(),
                CreateAspectManagement(),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.CurrentTarget.HasAura("Hunter's Mark"),
                    CastSpell("Hunter's Mark")),

                new Decorator(ret => StyxWoW.Me.Pet != null && StyxWoW.Me.Pet.CurrentTargetGuid != StyxWoW.Me.CurrentTargetGuid,
                    new Action(ret => { Lua.DoString("PetAttack()"); return RunStatus.Failure; })),

                new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Serpent Sting"),
                    CastSpell("Serpent Sting")),
                    
                new Decorator(ret => !StyxWoW.Me.IsAutoAttacking,
                    new Action(ret => { Lua.DoString("StartAttack()"); return RunStatus.Failure; }))
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

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.IsFacing(StyxWoW.Me.CurrentTarget) && !StyxWoW.Me.IsMoving,
                    new Action(ret => { StyxWoW.Me.CurrentTarget.Face(); return RunStatus.Failure; })),

                CreatePetManagement(),
                CreateAspectManagement(),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && !StyxWoW.Me.CurrentTarget.HasAura("Hunter's Mark"),
                    CastSpell("Hunter's Mark")),

                new Decorator(ret => StyxWoW.Me.Pet != null && StyxWoW.Me.Pet.CurrentTargetGuid != StyxWoW.Me.CurrentTargetGuid,
                    new Action(ret => { Lua.DoString("PetAttack()"); return RunStatus.Failure; })),

                new Decorator(ret => SpellManager.CanCast("Kill Command"),
                    new Action(ret => { SpellManager.Cast("Kill Command"); return RunStatus.Failure; })),

                new Decorator(ret => HunterSettings.Instance.AutoBestialWrath && StyxWoW.Me.CurrentTarget != null && 
                                     (IsBossOrElite(StyxWoW.Me.CurrentTarget) || _cachedAttackerCount > 1) && SpellManager.CanCast("Bestial Wrath"),
                    CastSpell("Bestial Wrath")),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && IsBossOrElite(StyxWoW.Me.CurrentTarget) && SpellManager.CanCast("Intimidation"),
                    CastSpell("Intimidation")),

                // ------------------------------------------------------
                // THE STUTTER-STEP KITING ENGINE
                // ------------------------------------------------------
                new Decorator(ret => HunterSettings.Instance.EnableKiting && StyxWoW.Me.CurrentTarget != null && 
                                     StyxWoW.Me.CurrentTarget.Distance <= HunterSettings.Instance.KiteDistance && 
                                     !StyxWoW.Me.CurrentTarget.IsCasting, 
                    new PrioritySelector(
                        
                        // 1. Snare Weaving (Apply slows before generating distance)
                        new Decorator(ret => StyxWoW.Me.CurrentTarget.Distance <= 5 && !StyxWoW.Me.CurrentTarget.HasAura("Wing Clip") && SpellManager.CanCast("Wing Clip"),
                            CastSpell("Wing Clip")),
                            
                        new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Concussive Shot") && !StyxWoW.Me.CurrentTarget.HasAura("Wing Clip") && SpellManager.CanCast("Concussive Shot"),
                            CastSpell("Concussive Shot")),

                        // 2. Instant Casts on the Move (Arcane Shot / Serpent Sting)
                        new Decorator(ret => StyxWoW.Me.CurrentTarget.HealthPercent <= 20 && SpellManager.CanCast("Kill Shot"), CastSpell("Kill Shot")),
                        new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Serpent Sting") && SpellManager.CanCast("Serpent Sting"), CastSpell("Serpent Sting")),
                        new Decorator(ret => SpellManager.CanCast("Arcane Shot"), CastSpell("Arcane Shot")),

                        // 3. Stutter-Step Vector Math
                        new Action(ret =>
                        {
                            double timeSincePhase = (DateTime.Now - _kitePhaseTimer).TotalMilliseconds;
                            
                            // Phase 1: Stop and Shoot (0.5 seconds)
                            if (_isStoppingToShoot)
                            {
                                if (StyxWoW.Me.IsMoving) WoWMovement.MoveStop();
                                
                                if (timeSincePhase > 500) // 500ms allows the auto-shot to clear the chamber
                                {
                                    _isStoppingToShoot = false;
                                    _kitePhaseTimer = DateTime.Now;
                                }
                                return RunStatus.Success; // Force routine to pause here while we shoot
                            }
                            
                            // Phase 2: Run Away (1.5 seconds)
                            if (timeSincePhase > 1500)
                            {
                                _isStoppingToShoot = true;
                                _kitePhaseTimer = DateTime.Now;
                                return RunStatus.Success;
                            }

                            // Generate Escape Vector
                            WoWPoint myLoc = StyxWoW.Me.Location;
                            WoWPoint tarLoc = StyxWoW.Me.CurrentTarget.Location;
                            double angle = Math.Atan2(myLoc.Y - tarLoc.Y, myLoc.X - tarLoc.X);
                            
                            float escapeX = myLoc.X + (float)Math.Cos(angle) * 15f;
                            float escapeY = myLoc.Y + (float)Math.Sin(angle) * 15f;
                            WoWPoint escapePoint = new WoWPoint(escapeX, escapeY, myLoc.Z);

                            // Validate with Nav Mesh
                            if (Navigator.CanNavigateFully(myLoc, escapePoint))
                            {
                                WoWMovement.ClickToMove(escapePoint);
                                return RunStatus.Success;
                            }

                            return RunStatus.Failure; // Yield if cornered so melee fallback handles it
                        })
                    )),

                // ------------------------------------------------------
                // MELEE FALLBACK (If Kiting is disabled or we are cornered)
                // ------------------------------------------------------
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.DistanceSqr <= 25,
                    new PrioritySelector(
                        CastSpell("Mongoose Bite"),
                        CastSpell("Raptor Strike"),
                        new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Wing Clip"), CastSpell("Wing Clip"))
                    )),

                // ------------------------------------------------------
                // STANDARD RANGED ROTATION
                // ------------------------------------------------------
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.DistanceSqr > 25,
                    new PrioritySelector(
                        new Decorator(ret => StyxWoW.Me.IsMoving, new Action(ret => { WoWMovement.MoveStop(); return RunStatus.Failure; })),
                        new Decorator(ret => StyxWoW.Me.CurrentTarget.HealthPercent <= 20, CastSpell("Kill Shot")),
                        new Decorator(ret => !StyxWoW.Me.CurrentTarget.HasAura("Serpent Sting"), CastSpell("Serpent Sting")),
                        CastSpell("Arcane Shot"),
                        new Decorator(ret => _cachedAttackerCount > 1, CastSpell("Multi-Shot")),
                        CastSpell("Steady Shot")
                    ))
            );
        }

        // ------------------------------------------------------
        // BEHAVIOR HELPERS
        // ------------------------------------------------------
        private Composite CreatePetManagement()
        {
            return new Decorator(ret => HunterSettings.Instance.AutoPetTriage,
                new PrioritySelector(
                    new Decorator(ret => StyxWoW.Me.Pet == null && SpellManager.CanCast("Call Pet"),
                        CastSpell("Call Pet")),
                    
                    new Decorator(ret => StyxWoW.Me.Pet != null && !StyxWoW.Me.Pet.IsAlive && SpellManager.CanCast("Revive Pet"),
                        new Action(ret => {
                            if (StyxWoW.Me.IsMoving) WoWMovement.MoveStop();
                            SpellManager.Cast("Revive Pet");
                            return RunStatus.Success;
                        })),

                    new Decorator(ret => StyxWoW.Me.Pet != null && StyxWoW.Me.Pet.IsAlive && 
                                         StyxWoW.Me.Pet.HealthPercent < HunterSettings.Instance.MendPetPercent && 
                                         !StyxWoW.Me.Pet.HasAura("Mend Pet") && SpellManager.CanCast("Mend Pet"),
                        CastSpell("Mend Pet"))
                ));
        }

        private Composite CreateAspectManagement()
        {
            return new Decorator(ret => HunterSettings.Instance.AutoAspects,
                new PrioritySelector(
                    new Decorator(ret => StyxWoW.Me.ManaPercent < HunterSettings.Instance.ViperManaPercent && !StyxWoW.Me.HasAura("Aspect of the Viper"),
                        CastSpell("Aspect of the Viper")),

                    new Decorator(ret => StyxWoW.Me.ManaPercent > HunterSettings.Instance.HawkManaPercent && StyxWoW.Me.HasAura("Aspect of the Viper"),
                        new PrioritySelector(
                            CastSpell("Aspect of the Dragonhawk"),
                            CastSpell("Aspect of the Hawk")
                        ))
                ));
        }

        // ------------------------------------------------------
        // THE GOD-LOOP (Pre-Filtered Native C# Scanner)
        // ------------------------------------------------------
        private void UpdateEnvironment()
        {
            if ((DateTime.Now - _lastEnvironmentUpdate).TotalMilliseconds < 200) return;

            _cachedAttackerCount = 0;

            foreach (WoWUnit u in ObjectManager.GetObjectsOfType<WoWUnit>())
            {
                if (u == null || !u.IsValid || !u.IsHostile || !u.IsAlive) continue;

                if (u.DistanceSqr < 900)
                {
                    if (u.IsTargetingMeOrPet)
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
        private bool IsBossOrElite(WoWUnit u)
        {
            return u != null && (u.IsBoss || u.Elite || u.MaxHealth > (StyxWoW.Me.MaxHealth * 3));
        }

        private Composite CastSpell(string spellName)
        {
            return new Decorator(ret => CustomCanCast(spellName),
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

            if (HunterSettings.Instance.EnableSpellQueueing)
            {
                if (SpellManager.Spells.ContainsKey(spellName))
                {
                    var cd = SpellManager.Spells[spellName].CooldownTimeLeft.TotalMilliseconds;
                    if (cd > 0 && cd <= HunterSettings.Instance.SpellQueueWindow)
                    {
                        return true; 
                    }
                }
            }
            return false;
        }
    }
}