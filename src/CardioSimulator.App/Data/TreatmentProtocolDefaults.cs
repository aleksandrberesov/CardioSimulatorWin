using System.Collections.Generic;
using CardioSimulator.Core.Domain.Treatment;

namespace CardioSimulator.App.Data;

/// <summary>
/// The built-in seed content for the Treatment Protocols screen, ported from the customer's
/// "логика перехода ритмов" mock-up. Used to populate the store on first run and to restore it via
/// the editor's "Reset to defaults". Bilingual EN/RU; the other three languages fall back to English.
/// </summary>
public static class TreatmentProtocolDefaults
{
    private static LocText T(string en, string ru) => new(en, ru);
    private static ActionItem A(ActionCategory c, string en, string ru) => new() { Category = c, Text = T(en, ru) };
    private static ResultItem R(RhythmKind k, string en, string ru, ClinicalRhythmState? state = null, double weight = 1) =>
        new() { Kind = k, Text = T(en, ru), State = state, Weight = weight };

    private static ResultItem Sinus(double weight = 1) =>
        R(RhythmKind.Normal, "Sinus rhythm", "Синусовый ритм", ClinicalRhythmState.Sinus, weight);

    public static TreatmentProtocolSet Build() => new()
    {
        Transitions = BuildTransitions(),
        Rules = BuildRules(),
        AclsSteps = BuildAcls(),
        Timings = BuildTimings(),
        Dosages = BuildDosages(),
    };

    // Effect timings (real clinical seconds) parsed from the human "time" column, for the engine binding.
    private const int Instant = 0, Sec20 = 20, Min2_3 = 150, Min5_10 = 450, Min10_20 = 900, Min15 = 600, Min45 = 2700, Min3 = 180;

    private static List<TransitionProtocol> BuildTransitions() => new()
    {
        // VF + defibrillation: success → sinus (row 1) / failure → asystole (row 2). The bridge merges rows that
        // share (rhythm, action) into one weighted engine rule: (VF, Defib) → sinus .75 / asystole .25.
        Tr(RhythmKind.Danger, "Ventricular fibrillation (VF)", "Фибрилляция желудочков (ФЖ)",
            new[] { A(ActionCategory.Elec, "Defib 200 J", "ДФБ 200 Дж") }, new[] { Sinus(0.75) },
            "Instant", "Мгновенно", "Success 70–80%", "Успех 70–80%",
            ClinicalRhythmState.VentricularFibrillation, TransitionTrigger.Defibrillation, null, Instant),
        Tr(RhythmKind.Danger, "VF", "ФЖ",
            new[] { A(ActionCategory.Elec, "Defib 200 J", "ДФБ 200 Дж") },
            new[] { R(RhythmKind.Danger, "Asystole", "Асистолия", ClinicalRhythmState.Asystole, 0.25) },
            "Instant", "Мгновенно", "Failure 20–30%", "Неудача 20–30%",
            ClinicalRhythmState.VentricularFibrillation, TransitionTrigger.Defibrillation, null, Instant),
        // Display-only teaching sequence (adrenaline → CPR → shock); the shock is already bound above.
        Tr(RhythmKind.Danger, "VF", "ФЖ",
            new[]
            {
                A(ActionCategory.Med, "Adrenaline 1 mg", "Адреналин 1 мг"),
                A(ActionCategory.Mech, "CPR 2 min", "СЛР 2 мин"),
                A(ActionCategory.Elec, "Defib 200 J", "ДФБ 200 Дж"),
            },
            new[] { Sinus() }, "2–3 min", "2–3 мин", "Improves defib success", "Повышает шанс успеха ДФБ",
            null, TransitionTrigger.None, null, Min2_3),
        Tr(RhythmKind.Danger, "VF", "ФЖ",
            new[] { A(ActionCategory.Med, "Amiodarone 300 mg", "Амиодарон 300 мг") },
            new[] { R(RhythmKind.Warning, "VT", "ЖТ", ClinicalRhythmState.VentricularTachycardia, 0.4), Sinus(0.5) },
            "5–10 min", "5–10 мин", "After 3rd failed defib", "После 3-го неудачного ДФБ",
            ClinicalRhythmState.VentricularFibrillation, TransitionTrigger.Drug, TreatmentDrug.Amiodarone, Min5_10),
        Tr(RhythmKind.Warning, "Ventricular tachycardia (VT) with pulse", "Желудочковая тахикардия (ЖТ) с пульсом",
            new[] { A(ActionCategory.Elec, "Sync cardioversion 100 J", "Синхр. кардиоверсия 100 Дж") },
            new[] { Sinus(0.9) }, "Instant", "Мгновенно", "Success 85–90%", "Успех 85–90%",
            ClinicalRhythmState.VentricularTachycardia, TransitionTrigger.SyncCardioversion, null, Instant),
        Tr(RhythmKind.Warning, "VT with pulse", "ЖТ с пульсом",
            new[] { A(ActionCategory.Med, "Amiodarone 150 mg", "Амиодарон 150 мг") },
            new[] { Sinus(0.85) }, "10–20 min", "10–20 мин", "Slow effect", "Медленный эффект",
            ClinicalRhythmState.VentricularTachycardia, TransitionTrigger.Drug, TreatmentDrug.Amiodarone, Min10_20),
        Tr(RhythmKind.Warning, "VT with pulse", "ЖТ с пульсом",
            new[] { A(ActionCategory.Elec, "Defib 200 J (unsync)", "ДФБ 200 Дж (асинхронно)") },
            new[] { R(RhythmKind.Danger, "VF", "ФЖ", ClinicalRhythmState.VentricularFibrillation, 0.8) },
            "Instant", "Мгновенно", "⚠ Error! R-on-T risk", "⚠ Ошибка! Риск R-on-T",
            ClinicalRhythmState.VentricularTachycardia, TransitionTrigger.Defibrillation, null, Instant),
        Tr(RhythmKind.Warning, "Atrial fibrillation (AF)", "Фибрилляция предсердий (ФП)",
            new[] { A(ActionCategory.Elec, "Sync cardioversion 200 J", "Синхр. кардиоверсия 200 Дж") },
            new[] { Sinus(0.75) }, "Instant", "Мгновенно", "Success 70–80%", "Успех 70–80%",
            ClinicalRhythmState.AtrialFibrillation, TransitionTrigger.SyncCardioversion, null, Instant),
        Tr(RhythmKind.Warning, "AF", "ФП",
            new[] { A(ActionCategory.Med, "Amiodarone 300 mg", "Амиодарон 300 мг") },
            new[] { Sinus(0.7) }, "30–60 min", "30–60 мин", "Slow conversion", "Медленная конверсия",
            ClinicalRhythmState.AtrialFibrillation, TransitionTrigger.Drug, TreatmentDrug.Amiodarone, Min45),
        Tr(RhythmKind.Warning, "AF", "ФП",
            new[] { A(ActionCategory.Med, "Metoprolol 5 mg", "Метопролол 5 мг") },
            new[] { R(RhythmKind.Warning, "AF, rate ↓", "ФП с ЧЖС ↓", ClinicalRhythmState.AtrialFibrillationRateControlled, 1.0) },
            "5–10 min", "5–10 мин", "Rate control, not conversion", "Контроль ЧСС, не конверсия",
            ClinicalRhythmState.AtrialFibrillation, TransitionTrigger.Drug, TreatmentDrug.Metoprolol, Min5_10),
        Tr(RhythmKind.Danger, "Asystole", "Асистолия",
            new[]
            {
                A(ActionCategory.Med, "Adrenaline 1 mg", "Адреналин 1 мг"),
                A(ActionCategory.Mech, "CPR 2 min", "СЛР 2 мин"),
            },
            new[] { R(RhythmKind.Danger, "VF / pulseless VT", "ФЖ/бЖТ", ClinicalRhythmState.VentricularFibrillation, 0.1) },
            "2–3 min", "2–3 мин", "Rare, but possible", "Редко, но возможно",
            ClinicalRhythmState.Asystole, TransitionTrigger.Drug, TreatmentDrug.Adrenaline, Min3),
        // Display-only: defibrillation of asystole is contraindicated (the validator blocks it).
        Tr(RhythmKind.Danger, "Asystole", "Асистолия",
            new[] { A(ActionCategory.Elec, "Defib", "ДФБ") },
            new[] { R(RhythmKind.Danger, "Asystole", "Асистолия") },
            "Instant", "Мгновенно", "⚠ Ineffective!", "⚠ Неэффективно!",
            null, TransitionTrigger.None, null, Instant),
        Tr(RhythmKind.Warning, "Third-degree AV block", "АВ-блокада III степени",
            new[] { A(ActionCategory.Med, "Atropine 0.5 mg", "Атропин 0.5 мг") },
            new[] { Sinus(0.1) }, "5–10 min", "5–10 мин", "If block is functional", "Если блокада функциональная",
            ClinicalRhythmState.CompleteAvBlock, TransitionTrigger.Drug, TreatmentDrug.Atropine, Min5_10),
        Tr(RhythmKind.Warning, "Third-degree AV block", "АВ-блокада III степени",
            new[] { A(ActionCategory.Mech, "Pacing 70 bpm, 50 mA", "ЭКС 70 уд/мин, 50 мА") },
            new[] { R(RhythmKind.Normal, "Paced rhythm", "Искусственный ритм", ClinicalRhythmState.Paced, 1.0) },
            "Instant", "Мгновенно", "On myocardial capture", "При захвате миокарда",
            ClinicalRhythmState.CompleteAvBlock, TransitionTrigger.Pacing, null, Instant),
        Tr(RhythmKind.Warning, "SVT (supraventricular tachycardia)", "СВТ (наджелудочковая тахикардия)",
            new[] { A(ActionCategory.Vagal, "Valsalva maneuver", "Проба Вальсальвы") },
            new[] { Sinus(0.22) }, "Instant", "Мгновенно", "Success 20–25%", "Успех 20–25%",
            ClinicalRhythmState.Svt, TransitionTrigger.Vagal, null, Instant),
        Tr(RhythmKind.Warning, "SVT", "СВТ",
            new[] { A(ActionCategory.Med, "Adenosine 6 mg", "Аденозин 6 мг") },
            new[] { Sinus(0.92) }, "10–30 sec", "10–30 сек", "Success 90–95%", "Успех 90–95%",
            ClinicalRhythmState.Svt, TransitionTrigger.Drug, TreatmentDrug.Adenosine, Sec20),
        Tr(RhythmKind.Warning, "SVT", "СВТ",
            new[] { A(ActionCategory.Elec, "Sync cardioversion 50 J", "Синхр. кардиоверсия 50 Дж") },
            new[] { Sinus(0.95) }, "Instant", "Мгновенно", "If unstable", "При нестабильности",
            ClinicalRhythmState.Svt, TransitionTrigger.SyncCardioversion, null, Instant),
        Tr(RhythmKind.Warning, "Torsades de pointes", "Пируэтная тахикардия (Torsades)",
            new[] { A(ActionCategory.Med, "Magnesium sulfate 2 g", "Магния сульфат 2 г") },
            new[] { Sinus(0.85) }, "5–15 min", "5–15 мин", "Drug of choice", "Препарат выбора",
            ClinicalRhythmState.Torsades, TransitionTrigger.Drug, TreatmentDrug.MagnesiumSulfate, Min15),
        Tr(RhythmKind.Warning, "Torsades", "Torsades",
            new[] { A(ActionCategory.Elec, "Defib 200 J", "ДФБ 200 Дж") },
            new[] { R(RhythmKind.Danger, "VF", "ФЖ", ClinicalRhythmState.VentricularFibrillation, 0.4), Sinus(0.6) },
            "Instant", "Мгновенно", "If unstable", "Если нестабильна",
            ClinicalRhythmState.Torsades, TransitionTrigger.Defibrillation, null, Instant),
    };

    private static TransitionProtocol Tr(RhythmKind currentKind, string curEn, string curRu,
        IEnumerable<ActionItem> actions, IEnumerable<ResultItem> results,
        string timeEn, string timeRu, string condEn, string condRu,
        ClinicalRhythmState? fromState, TransitionTrigger trigger, TreatmentDrug? triggerDrug, int effectSeconds) => new()
    {
        CurrentKind = currentKind,
        Current = T(curEn, curRu),
        Actions = new List<ActionItem>(actions),
        Results = new List<ResultItem>(results),
        Time = T(timeEn, timeRu),
        Conditions = T(condEn, condRu),
        FromState = fromState,
        Trigger = trigger,
        TriggerDrug = triggerDrug,
        EffectSeconds = effectSeconds,
    };

    private static List<ValidationRule> BuildRules() => new()
    {
        Rule("Defib in asystole:", "ДФБ при асистолии:",
            "Block the button or show the warning “Defibrillation is not indicated in asystole”. Effect: the rhythm does not change.",
            "Заблокировать кнопку или показать предупреждение “Дефибрилляция не показана при асистолии”. Эффект: ритм не меняется."),
        Rule("Unsynchronized defib in VT with pulse:", "Асинхронный ДФБ при ЖТ с пульсом:",
            "Warning “R-on-T risk → VF”. If the user confirms — transition to VF.",
            "Предупреждение “Риск R-on-T → ФЖ”. Если пользователь подтверждает — переход в ФЖ."),
        Rule("Adrenaline in VF/asystole:", "Адреналин при ФЖ/асистолии:",
            "Only in combination with CPR. Without CPR the effect is reduced by 50%.",
            "Только в комбинации с СЛР. Без СЛР эффект снижается на 50%."),
        Rule("Amiodarone:", "Амиодарон:",
            "Maximum daily dose 2.2 g. Exceeding it → risk of bradycardia/asystole.",
            "Максимальная суточная доза 2.2 г. Превышение → риск брадикардии/асистолии."),
        Rule("Atropine:", "Атропин:",
            "Maximum dose 3 mg. Overdose → tachycardia, delirium.",
            "Максимальная доза 3 мг. При передозировке → тахикардия, делирий."),
        Rule("Pacing:", "ЭКС:",
            "Current < 30 mA — no capture. Current > 150 mA — fibrillation risk. The rate must be higher than the patient's own rhythm.",
            "Ток < 30 мА — нет захвата. Ток > 150 мА — риск фибрилляции. Частота должна быть выше собственного ритма пациента."),
        Rule("CPR:", "СЛР:",
            "In VF/asystole — mandatory. Without CPR the defib success chance drops with each cycle.",
            "При ФЖ/асистолии — обязательна. Без СЛР шанс успеха ДФБ снижается с каждым циклом."),
        Rule("Oxygen/ventilation:", "Кислород/ИВЛ:",
            "In hypoxia (SpO2 < 90%) — the priority action. Without oxygenation the rhythm does not normalize.",
            "При гипоксии (SpO2 < 90%) — приоритетное действие. Без оксигенации ритм не нормализуется."),
    };

    private static ValidationRule Rule(string leadEn, string leadRu, string bodyEn, string bodyRu) =>
        new() { Lead = T(leadEn, leadRu), Body = T(bodyEn, bodyRu) };

    private static List<AclsStep> BuildAcls() => new()
    {
        Node(AclsNodeKind.Critical, "VF / pVT", "ФЖ / бЖТ", "Detected", "Обнаружена"),
        Node(AclsNodeKind.Default, "1. Defib 200 J", "1. ДФБ 200 Дж", "Immediately", "Немедленно"),
        Node(AclsNodeKind.Default, "2. CPR 2 min", "2. СЛР 2 мин", "+ Oxygen", "+ Кислород"),
        Node(AclsNodeKind.Default, "3. Rhythm check", "3. Проверка ритма", "If VF persists", "Если ФЖ persists"),
        Node(AclsNodeKind.Default, "4. Defib 200 J", "4. ДФБ 200 Дж", "+ Adrenaline 1 mg", "+ Адреналин 1 мг"),
        Node(AclsNodeKind.Default, "5. CPR 2 min", "5. СЛР 2 мин", "", ""),
        Node(AclsNodeKind.Default, "6. Defib 200 J", "6. ДФБ 200 Дж", "+ Amiodarone 300 mg", "+ Амиодарон 300 мг"),
        Node(AclsNodeKind.Normal, "Sinus rhythm", "Синусовый ритм", "✅ Success", "✅ Успех"),
    };

    private static AclsStep Node(AclsNodeKind kind, string titleEn, string titleRu, string subEn, string subRu) =>
        new() { Kind = kind, Title = T(titleEn, titleRu), Subtitle = T(subEn, subRu) };

    private static List<TimingLine> BuildTimings() => new()
    {
        Timing("Defib → rhythm assessment: instant", "ДФБ → оценка ритма: мгновенно"),
        Timing("Adrenaline → effect: 1–2 min (peak at 5 min)", "Адреналин → эффект: 1–2 мин (пик через 5 мин)"),
        Timing("Amiodarone → effect: 5–10 min (bolus), then infusion", "Амиодарон → эффект: 5–10 мин (болюс), затем инфузия"),
        Timing("CPR → continuous, pauses < 10 sec for defib", "СЛР → непрерывно, паузы < 10 сек для ДФБ"),
    };

    private static TimingLine Timing(string en, string ru) => new() { Text = T(en, ru) };

    private static List<DosageEntry> BuildDosages() => new()
    {
        Dosage("Adrenaline", "Адреналин", "VF/pVT, asystole, PEA", "ФЖ/бЖТ, асистолия, PEA",
            "1 mg", "1 мг", "IV bolus", "в/в болюс", "Every 3–5 min", "Каждые 3–5 мин"),
        Dosage("Amiodarone", "Амиодарон", "VF/pVT (refractory)", "ФЖ/бЖТ (рефрактерная)",
            "300 mg (1st), 150 mg (2nd)", "300 мг (1-я доза), 150 мг (2-я)", "IV bolus", "в/в болюс",
            "Max 2.2 g/day", "Макс 2.2 г/сут"),
        Dosage("Atropine", "Атропин", "Bradycardia, AV block", "Брадикардия, АВ-блокада",
            "0.5 mg", "0.5 мг", "IV bolus", "в/в болюс", "Every 3–5 min, max 3 mg", "Каждые 3–5 мин, макс 3 мг"),
        Dosage("Magnesium sulfate", "Магния сульфат", "Torsades de pointes", "Torsades de Pointes",
            "1–2 g", "1–2 г", "IV bolus", "в/в болюс", "Once", "Однократно"),
        Dosage("Adenosine", "Аденозин", "SVT", "СВТ", "6 mg (1st), 12 mg (2nd)", "6 мг (1-я), 12 мг (2-я)",
            "Rapid IV + flush", "в/в быстро + flush", "Max 2 doses", "Макс 2 дозы"),
        Dosage("Metoprolol", "Метопролол", "AF with high ventricular rate", "ФП с высокой ЧЖС",
            "5 mg", "5 мг", "Slow IV", "в/в медленно", "Every 5 min, max 15 mg", "Каждые 5 мин, макс 15 мг"),
        Dosage("Nitroglycerin", "Нитроглицерин", "Acute coronary syndrome", "Острый коронарный синдром",
            "0.4 mg", "0.4 мг", "Sublingual", "сублингвально", "Every 5 min, max 3 doses", "Каждые 5 мин, макс 3 дозы"),
        Dosage("Aspirin", "Аспирин", "ACS", "ОКС", "250–325 mg", "250–325 мг", "Oral (chew)", "per os (жевать)",
            "Once", "Однократно"),
    };

    private static DosageEntry Dosage(string drugEn, string drugRu, string indEn, string indRu,
        string doseEn, string doseRu, string routeEn, string routeRu, string repeatEn, string repeatRu) => new()
    {
        Drug = T(drugEn, drugRu),
        Indication = T(indEn, indRu),
        Dose = T(doseEn, doseRu),
        Route = T(routeEn, routeRu),
        Repeat = T(repeatEn, repeatRu),
    };
}
