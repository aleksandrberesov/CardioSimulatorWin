# Product Naming Options — Cardio Simulator

Date: 2026-09-03
Context: Current working names in use — "ESG Master" (likely a typo for "ECG Master") and generic "кардиосимулятор" / "Cardio Simulator". This document collects naming candidates explored for the product, with rationale, to support a decision with the partner.

## Recommendation

**RhythmiQ** — recommended primary name for the international market.

- Coined word (Rhythm + IQ): communicates the product's two core strengths — ECG rhythm recognition and adaptive/intelligent teaching.
- No known trademark conflicts in the cardiology/medtech space.
- Pronounces cleanly across English, German, Spanish and other markets relevant to international medical schools/clinics.
- Scales naturally into sub-brands matching existing app modes: `RhythmiQ Monitor`, `RhythmiQ Teaching`, `RhythmiQ Treatment`.

**Suggested branding:**
- Full name: `VLN RhythmiQ` or `RhythmiQ by VLN Advanced Health Simulations`
- Tagline (EN): *"Master cardiac rhythms with confidence"* or *"Where rhythm recognition becomes intuition"*
- Domains to check first: `rhythmiq.io`, `rhythmiq.app`, `getrhythmiq.com`

**Before finalizing:** check trademark registries (WIPO Global Brand DB for international, Rospatent/fips.ru if a Russian-market SKU is also planned) and confirm domain availability.

---

## Why not keep "ESG Master" / "ECG Master"

Names built as "ECG" + generic suffix ("Master", "Pro", "Trainer") are extremely common across existing ECG training software and hardware. Low uniqueness makes trademark registration weak and SEO/brand recall difficult. Recommend moving to a distinctive, ownable name.

---

## Full list of candidates considered

### International (Latin / English-facing)

| Name | Rationale |
|---|---|
| **RhythmiQ** ⭐ recommended | Rhythm + IQ — rhythm recognition + adaptive intelligence; distinctive, no known conflicts |
| **Cordis** | Latin *cor/cordis* = "heart". Short, premium, easy to pronounce internationally. **Risk:** conflicts with Cordis Corporation (Cardinal Health), an established cardiovascular device brand (stents, catheters) — same medical domain, real trademark/branding risk. Not recommended without legal clearance. |
| **CorPulse** | "Heart + pulse". Energetic, visually maps well to a pulse-wave logo mark. |
| **SinusLab** | References sinus rhythm (core ECG concept) + "lab" for hands-on practice framing. |
| **VectorCor** | Ties to the app's electrical axis (EOS) module + "heart"; techier positioning. |
| **PulseForge** | "Forging the pulse" — strong, trainer-like tone. |
| **CardioVista** | "Heart overview" — softer, premium feel. |
| **EchoRhythm** | Rhythm + "echo" evoking feedback/learning loop. |
| **MyoCore** | *Myo* (muscle) + core — technical, clinical tone. |
| **CardioSim Pro** | Considered but rejected — too generic, same weakness as "ECG Master". |

### Russian-facing

| Name | Rationale |
|---|---|
| **Кардиотор** | "Кардио" + "тренажёр" — clear, memorable, sounds native. Best RU candidate. |
| **ЭКГ‑Наставник** | Direct and clear, targets instructors specifically. |
| **Ритмоскоп** | "Rhythm scope" — sounds like a dedicated clinical instrument. |
| **Кардиополигон** | "Cardio training ground" — evokes scenario-based clinical practice. |
| **Сердечный Ритм.Про** | Literal option, weaker distinctiveness (same "Pro" suffix issue as ECG Master). |

---

## Decision checklist before locking a name

1. **Trademark search** — WIPO Global Brand DB (international) and Rospatent/fips.ru (if RU market is in scope).
2. **Domain availability** — check `.io`, `.app`, `.com`, `.ru` for top 2–3 finalists.
3. **Pronounceability** — validate in both English and Russian, and any other target-market language.
4. **No collision with real medical device/equipment brands** — especially important in cardiology (see Cordis note above).

## Implementation note

The app already has a single-point rebrand mechanism: `AppBrand*` constants in `Directory.Build.props`, with `AssemblyName` decoupled from the C# namespace, plus a data-folder migration path on first run after a rename. Once a name is finalized, applying it across the app (title bar, About screen, installer) is a small, contained change — not a refactor.
