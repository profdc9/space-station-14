# Saltern power: what we actually verified

Three rounds went into this. Everything below was observed in game or read out of the
tree at the pinned commit, not recalled. Coordinates are Saltern map tiles and are stable
across rounds, since every round loads the same map file.

## The shape of the problem

Engineering's SMES bank starts **empty** and has **no source**. That is the whole round in
one sentence.

| Fact | Evidence |
|---|---|
| Engineering SMES start at 0% | examined all three: `battery is 0% full` |
| Engineering substation starts at 0% | same |
| Solar array generates ~21 kW | the solar console's own readout |
| ...but engineering SMES reads 0 W in | read off the SMES menu while the array ran |
| Some station SMES sit pegged at 100% | never discharge, even while the rest drains |

A SMES at 100% that never discharges has its **OUT breaker off**. That is the leading
hypothesis for where the solar power goes, and it is still **untested** - we never reached
one. Test it first next round.

## Access, which is the thing that wasted the most time

| Device | Access needed | Controls |
|---|---|---|
| **SMES** | **none** | IN breaker, OUT breaker, charge rate, discharge rate, stored |
| **Substation** | **none** | same menu - it is a battery too |
| **APC** | **Engineering** | Main Breaker only. No per-channel toggles in this build |
| Power monitoring console | none | **read-only** - zero buttons in its XAML |

The SMES needing no access is the single most useful fact here. A passenger can walk up to
the bank and control the whole grid's storage. APCs, which look like the obvious lever, are
gated and will just say "Insufficient access!".

`apc.yml:23` - `access: [["Engineering"]]`
`ApcSystem.cs:118` - `_accessReader.IsAllowed(...)`, no bypass but an emag.

## Generators

| Generator | Where | Fuel | Rated |
|---|---|---|---|
| S.U.P.E.R.P.A.C.M.A.N. | `47,1` engineering | **uranium sheets** | 50 kW |
| J.R.P.A.C.M.A.N. | `-21,-26` southwest | **welding fuel** | 8 kW |

Both start **unanchored** - wrench them down or they connect to nothing. SUPERPACMAN's
output is already set to HV, which is correct for feeding the SMES bank.

Uranium is the blocker on the big one and we never found any. Welding fuel is common
(there is a fuel tank at `46,5`), so the JRPACMAN is the realistic first source despite
being six times smaller.

## Useful coordinates

```
40,5  41,5  42,5   engineering SMES bank
42,1               engineering substation
41,4               solar control computer (needs power to use)
47,1               SUPERPACMAN
45,1               mechanical + electrical toolbox
46,5               fuel tank
47,5               autolathe
-21,-26            JRPACMAN
~-30,-32           SW solar array SMES - approximate, never reached
```

## Sequencing that we got wrong twice

1. **Use the solar console before killing SMES output.** The console runs on that power.
   Turn OUT off first and you are standing at a dead console with a full bank.
2. **Aim the panels before enabling tracking.** Tracking holds whatever angle it is given -
   set it first and it faithfully keeps the panels edge-on to the sun forever. Set angular
   velocity to 0, sweep the angle until output peaks, *then* enable tracking.
3. **OUT off + IN on makes a SMES accumulate** instead of feeding the station. That is the
   right posture while bootstrapping, as long as something is actually generating.

## Other things that cost us

- **Unpowered doors need a crowbar.** A dark station is not a locked one.
- The solar array is **outside**, in vacuum and zero-g. Its SMES is normally in a small
  pressurised room, but reaching it across open space in a jumpsuit is fatal. Mask on face
  and internals on before going anywhere near it - a tank in the backpack is not internals.
- Load-shedding to protect a reserve only helps if there **is** a reserve. Engineering's
  bank starts empty, so there was never anything to protect.
