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

namespace SimpleEleShaman
{
    public enum PauseKeyMode { None, LeftControl, LeftShift, LeftAlt }

    public class EleSettings : Styx.Helpers.Settings
    {
        private static EleSettings _instance;
        public static EleSettings Instance { get { return _instance ?? (_instance = new EleSettings()); } }

        public EleSettings() : base(Styx.Helpers.Logging.ApplicationPath + "\\Settings\\EleShamanSettings_" + StyxWoW.Me.Name + ".xml") { Load(); }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("1. Spell Queueing (Latency Optimizer)")]
        [DisplayName("Enable Spell Queueing")]
        public bool EnableSpellQueueing { get; set; }

        [Setting, Styx.Helpers.DefaultValue(300)]
        [Category("1. Spell Queueing (Latency Optimizer)")]
        [DisplayName("Queue Window (ms)")]
        public int SpellQueueWindow { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("2. Utility")]
        [DisplayName("Cure Toxins")]
        public bool CureToxins { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Use Chain Lightning")]
        public bool UseChainLightning { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Auto Interrupt")]
        public bool AutoInterrupt { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Purge Enemy Buffs")]
        public bool UsePurge { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("3. Combat Options")]
        [DisplayName("Disable Self-Heal in Group")]
        public bool DisableSelfHealInGroup { get; set; }

        [Setting, Styx.Helpers.DefaultValue(60)]
        [Category("3. Combat Options")]
        [DisplayName("Thunderstorm Mana (%)")]
        [Description("Fire Thunderstorm to regenerate mana when below this percentage.")]
        public int ThunderstormManaThreshold { get; set; }

        [Setting, Styx.Helpers.DefaultValue(false)]
        [Category("3. Combat Options")]
        [DisplayName("T10 2-Piece Bonus")]
        [Description("Enable this if you have the Tier 10 2-piece set bonus to properly track Elemental Mastery's cooldown.")]
        public bool HasTier10TwoPiece { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("4. Targeting")]
        [DisplayName("Auto Assist")]
        public bool AutoAssist { get; set; }

        [Setting, Styx.Helpers.DefaultValue(true)]
        [Category("5. Totems")]
        [DisplayName("Auto Totems")]
        public bool AutoTotems { get; set; }

        [Setting, Styx.Helpers.DefaultValue(PauseKeyMode.LeftControl)]
        [Category("6. Hotkeys & Overrides")]
        [DisplayName("Pause Modifier Key")]
        [Description("Hold this key to temporarily pause the combat routine so you can move or manually cast.")]
        public PauseKeyMode PauseModifierKey { get; set; }
    }

    public class EleShamanCR : CombatRoutine
    {
        public override string Name { get { return "Smart Ele Leveling (Max Perf)"; } }
        public override WoWClass Class { get { return WoWClass.Shaman; } }
        public override bool NeedRest { get { return StyxWoW.Me.HealthPercent < 40 || StyxWoW.Me.ManaPercent < 30; } }
        public override double? PullDistance { get { return 28.0; } }

        public override bool WantButton { get { return true; } }
        public override void OnButtonPress()
        {
            Form configForm = new Form { Text = "Elemental Shaman Settings", Width = 420, Height = 560, StartPosition = FormStartPosition.CenterScreen };
            PropertyGrid grid = new PropertyGrid { SelectedObject = EleSettings.Instance, Dock = DockStyle.Fill };
            configForm.Controls.Add(grid);
            configForm.FormClosing += (s, e) => EleSettings.Instance.Save();
            configForm.ShowDialog();
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        private const int VK_LCONTROL = 0xA2;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_LMENU = 0xA4;

        private bool IsPauseModifierPressed()
        {
            switch (EleSettings.Instance.PauseModifierKey)
            {
                case PauseKeyMode.LeftControl: return (GetAsyncKeyState(VK_LCONTROL) & 0x8000) != 0;
                case PauseKeyMode.LeftShift: return (GetAsyncKeyState(VK_LSHIFT) & 0x8000) != 0;
                case PauseKeyMode.LeftAlt: return (GetAsyncKeyState(VK_LMENU) & 0x8000) != 0;
                default: return false;
            }
        }

        private DateTime _lastEnvironmentUpdate = DateTime.MinValue;
        private DateTime _lastElementalMasteryCast = DateTime.MinValue; 
        private List<WoWPlayer> _cachedGroup = new List<WoWPlayer>();
        private WoWUnit _cachedAssistTarget = null;
        private int _cachedAttackerCount = 0;
        private WoWPlayer _cachedCureTarget = null;
        private bool _isInParty = false;
        private bool _groupInCombat = false;

        private Composite _combatBehavior;
        public override Composite CombatBehavior { get { return _combatBehavior ?? (_combatBehavior = CreateCombatBehavior()); } }

        private Composite _pullBehavior;
        public override Composite PullBehavior { get { return _pullBehavior ?? (_pullBehavior = CreatePullBehavior()); } }

        private Composite _restBehavior;
        public override Composite RestBehavior { get { return _restBehavior ?? (_restBehavior = CreateRestBehavior()); } }

        private Composite CreateRestBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),
                new Decorator(ret => StyxWoW.Me.HasAura("Food") || StyxWoW.Me.HasAura("Drink"), new Action(ret => RunStatus.Success)),
                new Decorator(ret => EleSettings.Instance.CureToxins && _cachedCureTarget != null && SpellManager.CanCast("Cure Toxins"),
                    new Action(ret => {
                        if (StyxWoW.Me.IsMoving) WoWMovement.MoveStop();
                        if (StyxWoW.Me.CurrentTargetGuid != _cachedCureTarget.Guid) _cachedCureTarget.Target();
                        SpellManager.Cast("Cure Toxins");
                        return RunStatus.Success;
                    })),
                new Decorator(ret => StyxWoW.Me.HealthPercent < 70 && (!EleSettings.Instance.DisableSelfHealInGroup || !_isInParty),
                    new PrioritySelector(CastSpell("Lesser Healing Wave"), CastSpell("Healing Wave"))),
                new Decorator(ret => !HasWeaponImbue(), new PrioritySelector(CastSpell("Flametongue Weapon"), CastSpell("Rockbiter Weapon")))
            );
        }

        private Composite CreatePullBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),
                new Decorator(ret => HasTotems(), CastSpell("Totemic Recall")),
                new Decorator(ret => !HasWeaponImbue(), new PrioritySelector(CastSpell("Flametongue Weapon"), CastSpell("Rockbiter Weapon"))),
                new Decorator(ret => !StyxWoW.Me.HasAura("Water Shield") && !StyxWoW.Me.HasAura("Lightning Shield"),
                    new PrioritySelector(
                        new Decorator(ret => _isInParty || StyxWoW.Me.ManaPercent < 30, CastSpell("Water Shield")),
                        CastSpell("Lightning Shield")
                    )),
                CastSpell("Lightning Bolt")
            );
        }

        private Composite CreateCombatBehavior()
        {
            return new PrioritySelector(
                new Decorator(ret => IsPauseModifierPressed(), new Action(ret => RunStatus.Success)),
                new Action(ret => { UpdateEnvironment(); return RunStatus.Failure; }),
                
                new Decorator(ret => EleSettings.Instance.AutoAssist && (StyxWoW.Me.CurrentTarget == null || !StyxWoW.Me.CurrentTarget.IsAlive || !StyxWoW.Me.CurrentTarget.IsHostile),
                    new Action(ret => {
                        if (_cachedAssistTarget != null)
                        {
                            _cachedAssistTarget.Target();
                            return RunStatus.Success;
                        }
                        return RunStatus.Failure;
                    })),

                new Decorator(ret => StyxWoW.Me.HealthPercent < 50 && (!EleSettings.Instance.DisableSelfHealInGroup || !_isInParty),
                    new PrioritySelector(
                        new Decorator(ret => StyxWoW.Me.HealthPercent < 35, CastSpell("Gift of the Naaru")),
                        CastSpell("Lesser Healing Wave"),
                        CastSpell("Healing Wave")
                    )),

                new Decorator(ret => EleSettings.Instance.CureToxins && _cachedCureTarget != null && SpellManager.CanCast("Cure Toxins"),
                    new Action(ret => {
                        if (StyxWoW.Me.IsMoving) WoWMovement.MoveStop();
                        if (StyxWoW.Me.CurrentTargetGuid != _cachedCureTarget.Guid) _cachedCureTarget.Target();
                        SpellManager.Cast("Cure Toxins");
                        return RunStatus.Success;
                    })),

                new Decorator(ret => EleSettings.Instance.AutoInterrupt && StyxWoW.Me.CurrentTarget != null && (StyxWoW.Me.CurrentTarget.IsCasting || StyxWoW.Me.CurrentTarget.IsChanneling) && StyxWoW.Me.CurrentTarget.CanInterruptCurrentSpellCast,
                    new PrioritySelector(
                        new Decorator(ret => SpellManager.CanCast("Wind Shear"),
                            new Action(ret => {
                                Logging.Write("Interrupting with Wind Shear (Off-GCD)");
                                SpellManager.Cast("Wind Shear");
                                return RunStatus.Failure; 
                            })),
                        CastSpell("Earth Shock")
                    )),

                new Decorator(ret => EleSettings.Instance.UsePurge && StyxWoW.Me.CurrentTarget != null && HasPurgeableMagic() && SpellManager.CanCast("Purge"), CastSpell("Purge")),

                new Decorator(ret => _cachedAttackerCount > 1,
                    new PrioritySelector(
                        new Decorator(ret => EleSettings.Instance.AutoTotems && !HasTotems(), CastSpell("Call of the Elements")),
                        CastElementalMastery(),
                        new Decorator(ret => !_isInParty, CastSpell("Stoneclaw Totem")),
                        new Decorator(ret => StyxWoW.Me.CurrentTarget != null && StyxWoW.Me.CurrentTarget.DistanceSqr < 100, CastSpell("Thunderstorm")), 
                        new Decorator(ret => EleSettings.Instance.UseChainLightning && StyxWoW.Me.ManaPercent > 60, CastSpell("Chain Lightning"))
                    )),
                    
                new Decorator(ret => StyxWoW.Me.CurrentTarget != null && IsBossOrElite(StyxWoW.Me.CurrentTarget),
                    new PrioritySelector(
                        new Decorator(ret => EleSettings.Instance.AutoTotems && !HasTotems(), CastSpell("Call of the Elements")),
                        CastElementalMastery()
                    )),

                new Decorator(ret => !StyxWoW.Me.HasAura("Water Shield") && !StyxWoW.Me.HasAura("Lightning Shield"),
                    new PrioritySelector(
                        new Decorator(ret => _isInParty || StyxWoW.Me.ManaPercent < 30, CastSpell("Water Shield")),
                        CastSpell("Lightning Shield")
                    )),
                    
                new Decorator(ret => StyxWoW.Me.ManaPercent < EleSettings.Instance.ThunderstormManaThreshold, CastSpell("Thunderstorm")),

                new Decorator(ret => StyxWoW.Me.CurrentTarget != null,
                    new PrioritySelector(
                        new Decorator(ret => StyxWoW.Me.IsMoving,
                            new PrioritySelector(
                                new Decorator(ret => NeedsFlameShock(), CastSpell("Flame Shock")),
                                CastSpell("Earth Shock")
                            )),
                            
                        new Decorator(ret => !_isInParty && StyxWoW.Me.CurrentTarget.DistanceSqr < 144 && StyxWoW.Me.CurrentTarget.IsTargetingMeOrPet && !StyxWoW.Me.CurrentTarget.HasAura("Frost Shock"), CastSpell("Frost Shock")),
                        
                        new Decorator(ret => NeedsFlameShock(), CastSpell("Flame Shock")),
                        new Decorator(ret => StyxWoW.Me.CurrentTarget.HasAura("Flame Shock"), CastSpell("Lava Burst")),
                        new Decorator(ret => EleSettings.Instance.UseChainLightning && StyxWoW.Me.ManaPercent > 60, CastSpell("Chain Lightning")),
                        CastSpell("Lightning Bolt")
                    ))
            );
        }

        private Composite CastElementalMastery()
        {
            return new Decorator(ret => (DateTime.Now - _lastElementalMasteryCast).TotalSeconds >= 180 && SpellManager.CanCast("Elemental Mastery"),
                new Action(ret => {
                    Logging.Write("Casting: Elemental Mastery (Internal CD set to 180s)");
                    SpellManager.Cast("Elemental Mastery");
                    _lastElementalMasteryCast = DateTime.Now;
                    return RunStatus.Failure; 
                }));
        }

        private void UpdateEnvironment()
        {
            if ((DateTime.Now - _lastEnvironmentUpdate).TotalMilliseconds < 200) return;

            _cachedGroup.Clear();
            _cachedGroup.Add(StyxWoW.Me);
            _cachedAttackerCount = 0;
            _cachedAssistTarget = null;
            _cachedCureTarget = null;
            _groupInCombat = false;
            
            _isInParty = StyxWoW.Me.GroupInfo.IsInParty || StyxWoW.Me.GroupInfo.IsInRaid;
            double closestAssistDistSqr = 999999;

            foreach (WoWPlayer p in ObjectManager.GetObjectsOfType<WoWPlayer>())
            {
                if (p == null || !p.IsValid || !p.IsAlive || p.IsMe || !p.IsInMyPartyOrRaid) continue;

                if (p.DistanceSqr < 1600)
                {
                    _cachedGroup.Add(p);
                    if (p.Combat)
                    {
                        _groupInCombat = true;
                        if (p.CurrentTargetGuid != 0 && _cachedAssistTarget == null)
                        {
                            WoWUnit pt = ObjectManager.GetObjectByGuid<WoWUnit>(p.CurrentTargetGuid);
                            if (pt != null && pt.IsHostile && pt.IsAlive && pt.DistanceSqr < closestAssistDistSqr)
                            {
                                closestAssistDistSqr = pt.DistanceSqr;
                                _cachedAssistTarget = pt;
                            }
                        }
                    }
                }
            }

            foreach (WoWUnit u in ObjectManager.GetObjectsOfType<WoWUnit>())
            {
                if (u == null || !u.IsValid || !u.IsHostile || !u.IsAlive) continue;

                double distSqr = u.DistanceSqr; 
                if (distSqr >= 1600) continue;

                if (distSqr < 400)
                {
                    if (u.IsTargetingMeOrPet)
                    {
                        _cachedAttackerCount++;
                    }
                    else if (u.CurrentTargetGuid != 0)
                    {
                        if (_cachedGroup.Any(g => g.Guid == u.CurrentTargetGuid))
                        {
                            _cachedAttackerCount++;
                        }
                    }
                }

                if (_cachedAssistTarget == null && u.CanSelect && u.InLineOfSight && u.CurrentTargetGuid != 0)
                {
                    if (_cachedGroup.Any(g => g.Guid == u.CurrentTargetGuid && distSqr < closestAssistDistSqr))
                    {
                        closestAssistDistSqr = distSqr;
                        _cachedAssistTarget = u;
                    }
                }
            }

            if (EleSettings.Instance.CureToxins)
            {
                foreach (var p in _cachedGroup)
                {
                    if (HasNativePoisonOrDisease(p))
                    {
                        _cachedCureTarget = p;
                        break;
                    }
                }
            }

            _lastEnvironmentUpdate = DateTime.Now;
        }

        private Composite CastSpell(string spellName)
        {
            return new Decorator(ret => CustomCanCast(spellName),
                new Action(ret => {
                    if (spellName == "Lightning Bolt" || spellName == "Lava Burst" || spellName.Contains("Heal") || spellName == "Chain Lightning" || spellName == "Totemic Recall")
                    {
                        if (StyxWoW.Me.IsMoving) WoWMovement.MoveStop();
                    }
                    
                    // T10 2-Piece Subtraction: Subtract 2s from the C# internal timer on cast
                    if (EleSettings.Instance.HasTier10TwoPiece && (spellName == "Lightning Bolt" || spellName == "Chain Lightning"))
                    {
                        _lastElementalMasteryCast = _lastElementalMasteryCast.AddSeconds(-2);
                    }

                    Logging.Write(string.Format("Casting: {0}", spellName));
                    SpellManager.Cast(spellName);
                    return RunStatus.Success;
                }));
        }

        private bool CustomCanCast(string spellName)
        {
            if (!SpellManager.HasSpell(spellName)) return false;
            
            if (!StyxWoW.Me.IsCasting && !StyxWoW.Me.IsChanneling) 
                return SpellManager.CanCast(spellName);

            if (EleSettings.Instance.EnableSpellQueueing && StyxWoW.Me.IsCasting)
            {
                if (StyxWoW.Me.CurrentCastTimeLeft.TotalMilliseconds > 0 && StyxWoW.Me.CurrentCastTimeLeft.TotalMilliseconds <= EleSettings.Instance.SpellQueueWindow)
                {
                    if (SpellManager.Spells.ContainsKey(spellName) && SpellManager.Spells[spellName].CooldownTimeLeft.TotalMilliseconds == 0)
                    {
                        return true; 
                    }
                }
            }
            return false;
        }

        private bool NeedsFlameShock()
        {
            if (StyxWoW.Me.CurrentTarget == null) return false;
            if (!StyxWoW.Me.CurrentTarget.HasAura("Flame Shock")) return true;
            
            var auras = StyxWoW.Me.CurrentTarget.ActiveAuras;
            if (auras.ContainsKey("Flame Shock"))
            {
                var fs = auras["Flame Shock"];
                if (fs.CreatorGuid == StyxWoW.Me.Guid && fs.TimeLeft.TotalSeconds < 1.5)
                {
                    return true;
                }
            }
            return false;
        }

        private bool IsBossOrElite(WoWUnit u)
        {
            return u != null && (u.IsBoss || u.Elite || u.MaxHealth > (StyxWoW.Me.MaxHealth * 3));
        }

        private bool HasWeaponImbue()
        {
            var mainHand = StyxWoW.Me.Inventory.Equipped.MainHand;
            return mainHand != null && mainHand.TemporaryEnchantment != null && mainHand.TemporaryEnchantment.Id > 0;
        }

        private bool HasTotems() { return StyxWoW.Me.Totems.Any(t => t != null && t.Unit != null); }
        
        private bool HasTotem(string totemName) { return StyxWoW.Me.Totems.Any(t => t != null && t.Unit != null && t.Name == totemName); }

        private bool HasPurgeableMagic()
        {
            if (StyxWoW.Me.CurrentTarget == null) return false;
            foreach (WoWAura aura in StyxWoW.Me.CurrentTarget.ActiveAuras.Values)
            {
                if (aura.Spell.DispelType == WoWDispelType.Magic && !aura.IsHarmful) return true;
            }
            return false;
        }

        private bool HasNativePoisonOrDisease(WoWUnit unit)
        {
            foreach (WoWAura aura in unit.ActiveAuras.Values)
            {
                if (aura.Spell.DispelType == WoWDispelType.Poison || aura.Spell.DispelType == WoWDispelType.Disease) return true;
            }
            return false;
        }
    }
}