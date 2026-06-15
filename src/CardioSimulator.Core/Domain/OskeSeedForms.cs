using System.Linq;

namespace CardioSimulator.Core.Domain;

/// <summary>
/// The two built-in conclusion forms, transcribed verbatim from the accreditation passport §15
/// (Therapy — §15.1, 10 blocks; Cardiology/Functional Diagnostics — §15.2, 13 blocks). This is the
/// canonical source of the seed content; the bundled JSON shipped for first-run seeding is generated
/// from here (via <c>OskeJson</c>), so there is one source of truth. Once a teacher edits a form in
/// the constructor, the on-disk file takes over.
/// </summary>
public static class OskeSeedForms
{
    public const string Version = "2025.05";

    public static OskeForm For(OskeSpecialty specialty) =>
        OskeForms.FormIdFor(specialty) == OskeForms.TherapyFormId ? Therapy() : Cardiology(specialty);

    public static IReadOnlyList<OskeForm> All() => new[] { Therapy(), Cardiology(OskeSpecialty.Cardiology) };

    // ── Shared option groups (blocks common to both forms) ─────────────────────────────────

    private static OskeOption O(string id, string text) => new(id, text);

    private static IReadOnlyList<OskeOption> Rhythm() => new[]
    {
        O("sinus", "Синусовый"),
        O("low_atrial", "Нижнепредсердный"),
        O("pacemaker_migration", "Миграция водителя ритма по предсердиям"),
        O("afib", "Фибрилляция предсердий"),
        O("aflutter", "Трепетание предсердий"),
        O("avnrt", "АВУРТ"),
    };

    private static IReadOnlyList<OskeOption> HeartRate() => new[]
    {
        O("lt_50", "Менее 50"),
        O("from_50_to_101", "От 50 до 101"),
        O("gte_101", "101 и более"),
    };

    private static IReadOnlyList<OskeOption> Nzhes() => new[]
    {
        O("none", "Нет"),
        O("single", "Единичная НЖЭС"),
        O("couplet", "Куплет"),
        O("triplet", "Триплет"),
    };

    private static IReadOnlyList<OskeOption> Zhes() => new[]
    {
        O("none", "Нет"),
        O("rare_single", "Редкая одиночная ЖЭС"),
        O("couplet", "Куплет"),
        O("triplet", "Триплет"),
    };

    private static IReadOnlyList<OskeOption> AvConduction() => new[]
    {
        O("none", "Нет нарушений"),
        O("av_block_1", "Атриовентрикулярная блокада 1 степени"),
        O("av_block_2_mobitz_1", "Атриовентрикулярная блокада 2 степени Мобиц 1"),
        O("av_block_2_mobitz_2", "Атриовентрикулярная блокада 2 степени Мобиц 2"),
        O("av_block_3", "Атриовентрикулярная блокада 3 степени"),
        O("aflutter_variable", "Трепетание предсердий с переменным коэффициентом проведения"),
        O("aflutter", "Трепетание предсердий"),
        O("cannot_assess", "Невозможно оценить атриовентрикулярную проводимость"),
        O("accessory_pathway", "Имеются признаки дополнительного проводящего пути"),
    };

    private static IReadOnlyList<OskeOption> IvConduction() => new[]
    {
        O("none", "Нет нарушений"),
        O("rbbb", "Полная блокада правой ножки пучка Гиса (ПБПНПГ)"),
        O("lbbb", "Полная блокада левой ножки пучка Гиса (ПБЛНПГ)"),
        O("lafb", "Блокада передней ветви левой ножки пучка Гиса (БПВЛНПГ)"),
        O("delta_wave_qrs", "Увеличение длительности комплекса QRS из-за дельта-волны"),
    };

    private static IReadOnlyList<OskeOption> Lvh() => new[]
    {
        O("none", "Достоверных признаков гипертрофии левого желудочка нет"),
        O("present", "Имеются достоверные признаки гипертрофии левого желудочка"),
    };

    private static IReadOnlyList<OskeOption> PathologicalQ() => new[]
    {
        O("none", "Нет патологического Q зубца"),
        O("present", "Есть патологический Q зубец"),
    };

    private static IReadOnlyList<OskeOption> MiSt() => new[]
    {
        O("none", "Нет убедительных признаков острого, подострого инфаркта миокарда с подъёмом сегмента ST"),
        O("anteroseptal", "Передне-перегородочный"),
        O("anteroapical", "Передне-верхушечный"),
        O("extensive_anterior", "Распространённый передний"),
        O("inferior", "Нижний"),
        O("inferolateral", "Нижнебоковой"),
    };

    private static IReadOnlyList<OskeOption> AdditionalInfo() => new[]
    {
        O("none", "Нет"),
        O("complete_bbb", "Полная блокада ножки пучка Гиса"),
        O("wpw", "Синдром Вольфа-Паркинсона-Уайта (WPW)"),
        O("nstemi_possible", "Нельзя исключить ОКС без подъёма сегмента ST, необходима оценка клинического статуса (ОКСбпST)"),
        O("stemi_possible", "Нельзя исключить ОКС с подъёмом сегмента ST (ОКСпST)"),
    };

    // ── Form A — Терапия (§15.1, 10 blocks) ────────────────────────────────────────────────

    public static OskeForm Therapy()
    {
        var q = new List<OskeQuestion>
        {
            new("rhythm", 1, "Ритм", OskeAnswerKind.Single, Rhythm()),
            new("heart_rate", 2, "Минимальная и максимальная ЧСС (ЧСЖ)", OskeAnswerKind.Single, HeartRate()),
            new("nzhes", 3, "Наджелудочковая экстрасистолия (НЖЭС)", OskeAnswerKind.Single, Nzhes()),
            new("zhes", 4, "Желудочковая экстрасистолия (ЖЭС)", OskeAnswerKind.Single, Zhes()),
            new("av_conduction", 5, "Оценка атриовентрикулярной проводимости", OskeAnswerKind.Single, AvConduction()),
            new("iv_conduction", 6, "Оценка внутрижелудочковой проводимости", OskeAnswerKind.Single, IvConduction()),
            new("lvh", 7, "Оценка гипертрофии левого желудочка", OskeAnswerKind.Single, Lvh()),
            new("pathological_q", 8, "Наличие патологического зубца Q", OskeAnswerKind.Single, PathologicalQ()),
            new("mi_st", 9, "Признаки острого, подострого инфаркта миокарда с подъёмом сегмента ST (ИМпST)", OskeAnswerKind.Single, MiSt()),
            new("additional_info", 10, "Дополнительная информация по данной ЭКГ", OskeAnswerKind.Single, AdditionalInfo()),
        };
        return new OskeForm(OskeForms.TherapyFormId, OskeSpecialty.Therapy, Version, q);
    }

    // ── Form B — Кардиология / Функциональная диагностика (§15.2, 13 blocks) ────────────────

    public static OskeForm Cardiology(OskeSpecialty specialty)
    {
        var eos = new[]
        {
            O("normal", "В норме"),
            O("left", "Отклонение влево"),
            O("right", "Отклонение вправо"),
        };
        var stDynamics = new[]
        {
            O("none", "Нет"),
            O("depression", "Депрессия сегмента ST"),
            O("stemi_changes", "Изменения, характерные для ОКСпST"),
            O("bbb_depression", "Характерная для блокады ножки пучка Гиса депрессия сегмента ST"),
            O("bbb_elevation", "Характерная для блокады ножки пучка Гиса элевация сегмента ST"),
            O("arrhythmia_obscures", "Наличие аритмии затрудняет оценку сегмента ST"),
        };
        var tWave = new[]
        {
            O("none", "Нет нарушений"),
            O("tall_peaked", "Высокий заострённый"),
            O("negative", "Отрицательный"),
            O("bbb_changes", "Изменения зубца T, характерные для блокады ножки пучка Гиса"),
            O("arrhythmia_obscures", "Наличие аритмии затрудняет оценку зубца Т"),
            O("acs_changes", "Изменения зубца Т, характерные для ОКС"),
            O("biphasic", "Двухфазный Т зубец"),
        };

        var q = new List<OskeQuestion>
        {
            new("rhythm", 1, "Ритм", OskeAnswerKind.Single, Rhythm()),
            new("eos", 2, "Электрическая ось сердца (ЭОС)", OskeAnswerKind.Single, eos),
            new("heart_rate", 3, "Минимальная и максимальная ЧСС (ЧСЖ)", OskeAnswerKind.Single, HeartRate()),
            new("nzhes", 4, "Наджелудочковая экстрасистолия (НЖЭС)", OskeAnswerKind.Single, Nzhes()),
            new("zhes", 5, "Желудочковая экстрасистолия (ЖЭС)", OskeAnswerKind.Single, Zhes()),
            new("av_conduction", 6, "Оценка атриовентрикулярной проводимости", OskeAnswerKind.Single, AvConduction()),
            new("iv_conduction", 7, "Оценка внутрижелудочковой проводимости", OskeAnswerKind.Single, IvConduction()),
            new("lvh", 8, "Оценка гипертрофии левого желудочка", OskeAnswerKind.Single, Lvh()),
            new("st_dynamics", 9, "Динамика сегмента ST (возможно несколько ответов)", OskeAnswerKind.Multi, stDynamics),
            new("pathological_q", 10, "Наличие патологического зубца Q", OskeAnswerKind.Single, PathologicalQ()),
            new("t_wave", 11, "Оценка зубца Т", OskeAnswerKind.Single, tWave),
            new("mi_st", 12, "Признаки острого, подострого инфаркта миокарда с подъёмом сегмента ST (ИМпST)", OskeAnswerKind.Single, MiSt()),
            new("additional_info", 13, "Дополнительная информация по данной ЭКГ", OskeAnswerKind.Single, AdditionalInfo()),
        };
        return new OskeForm(OskeForms.CardiologyFormId, specialty, Version, q);
    }
}
