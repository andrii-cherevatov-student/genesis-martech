using System.Text.Json.Nodes;

namespace MarTechSolution.Services;

/// <summary>
/// JSON Schemas for structured outputs. Written to be valid for both OpenAI strict mode
/// (every property required, additionalProperties:false, nullable via type arrays) and Gemini.
/// </summary>
public static class Schemas
{
    const string Str = """{"type":"string"}""";
    const string StrArr = """{"type":"array","items":{"type":"string"}}""";
    const string NumOrNull = """{"type":["number","null"]}""";
    static readonly string RoleEnum = $$"""{"type":"string","enum":[{{string.Join(",", Roles.All.Select(r => $"\"{r}\""))}}]}""";

    static string Obj(string props, params string[] required) =>
        """{"type":"object","additionalProperties":false,"required":[""" + string.Join(",", required.Select(r => $"\"{r}\"")) +
        """],"properties":{""" + props + "}}";

    static string D(string type, string description) => $$"""{"type":"{{type}}","description":{{System.Text.Json.JsonSerializer.Serialize(description)}}}""";

    static readonly string Segment = Obj($$"""
        "id": {{D("string", "Segment id: C1, C2… for competitor videos; W1, W2… for our script.")}},
        "role": {{RoleEnum}},
        "startSec": {{NumOrNull}},
        "endSec": {{NumOrNull}},
        "observed": {{Obj($$"""
            "spokenText": {{D("string", "Verbatim voice-over/dialogue. Empty string if none.")}},
            "onScreenText": {{D("string", "Verbatim on-screen text/captions. Empty string if none.")}},
            "visualDescription": {{D("string", "Literal description: shot type, framing, action, props, edit pace, transitions.")}},
            "audioNotes": {{D("string", "Music, SFX, voice tone. Empty string if unknown.")}}
            """, "spokenText", "onScreenText", "visualDescription", "audioNotes")}},
        "interpretation": {{Obj($$"""
            "mechanism": {{D("string", "Persuasion mechanism, e.g. pattern interrupt, quantified pain, demonstration-as-proof, social proof.")}},
            "angle": {{D("string", "Advertising angle this segment pushes.")}},
            "emotionalTrigger": {{Str}},
            "storytellingPattern": {{Str}},
            "reusablePattern": {{D("string", "Product-agnostic, executable template with placeholders like [PRODUCT], [PAIN].")}},
            "evidence": {{D("string", "The specific observed details (with timestamps) that justify this interpretation.")}},
            "confidence": {{D("number", "0–1 confidence in the interpretation.")}}
            """, "mechanism", "angle", "emotionalTrigger", "storytellingPattern", "reusablePattern", "evidence", "confidence")}}
        """, "id", "role", "startSec", "endSec", "observed", "interpretation");

    static readonly string TechniqueSchema = Obj($$"""
        "id": {{D("string", "T1, T2… for competitor; WT1, WT2… for our script.")}},
        "name": {{D("string", "Short memorable name a creative team would use.")}},
        "category": {"type":"string","enum":["Hook","Visual","Editing","Sound","Storytelling","Persuasion","Proof","Offer","CTA"]},
        "description": {{Str}},
        "sourceSegmentIds": {{StrArr}},
        "startSec": {{NumOrNull}},
        "endSec": {{NumOrNull}},
        "evidence": {{Str}},
        "howToReuse": {{D("string", "Concrete, product-independent instructions a motion designer can execute.")}}
        """, "id", "name", "category", "description", "sourceSegmentIds", "startSec", "endSec", "evidence", "howToReuse");

    public static readonly JsonNode Analysis = JsonNode.Parse(Obj($$"""
        "title": {{Str}},
        "summary": {{D("string", "2–3 sentence strategic summary of how the ad sells.")}},
        "productCategory": {{Str}},
        "durationSec": {{NumOrNull}},
        "primaryAngle": {{Str}},
        "storytellingPattern": {{Str}},
        "targetAudience": {{Str}},
        "segments": {"type":"array","items":{{Segment}}},
        "techniques": {"type":"array","items":{{TechniqueSchema}}},
        "preserve": {"type":"array","items":{{Obj($$"""
            "element": {{Str}}, "segmentId": {{Str}}, "reason": {{Str}}
            """, "element", "segmentId", "reason")}}},
        "productBenefits": {{StrArr}}
        """, "title", "summary", "productCategory", "durationSec", "primaryAngle", "storytellingPattern",
        "targetAudience", "segments", "techniques", "preserve", "productBenefits"))!;

    static string Criterion(string description) => Obj($$"""
        "score": {{D("number", "0–10. " + description)}},
        "reason": {{D("string", "At most 10 words.")}}
        """, "score", "reason");

    public static readonly JsonNode Matches = JsonNode.Parse(Obj($$"""
        "candidates": {"type":"array","items":{{Obj($$"""
            "id": {{D("string", "M1, M2…")}},
            "competitorTechniqueId": {{Str}},
            "competitorSegmentId": {{Str}},
            "winnerSegmentId": {{Str}},
            "techniqueName": {{Str}},
            "criteria": {{Obj($$"""
                "role": {{Criterion("Does the technique do the same narrative job as the winner segment?")}},
                "pain": {{Criterion("Does it address the same or adjacent customer pain?")}},
                "mechanism": {{Criterion("Is the persuasion mechanism transferable to this segment without breaking it?")}},
                "productFit": {{Criterion("Can it be executed truthfully using only SockLock's approved facts?")}},
                "novelty": {{Criterion("How much does it add beyond what the winner already does? 0 = already does it.")}}
                """, "role", "pain", "mechanism", "productFit", "novelty")}},
            "whyTransferable": {{Str}},
            "whatToChange": {{D("string", "Concrete change to the winner segment (shot, line, overlay, pacing).")}},
            "whatToKeep": {{D("string", "What in the winner segment must stay unchanged and why.")}},
            "hypothesis": {{D("string", "Testable creative hypothesis: 'If we…, then [metric] will… because…'.")}},
            "compatible": {"type":"boolean"},
            "unsupportedClaimRisk": {"type":"boolean"},
            "directCopyRisk": {"type":"boolean"},
            "riskNotes": {{Str}},
            "expectedImpact": {"type":"string","enum":["Hook rate","Hold rate","CTR","Conversion rate","Rewatch rate"]}
            """, "id", "competitorTechniqueId", "competitorSegmentId", "winnerSegmentId", "techniqueName", "criteria",
            "whyTransferable", "whatToChange", "whatToKeep", "hypothesis", "compatible", "unsupportedClaimRisk",
            "directCopyRisk", "riskNotes", "expectedImpact")}}}
        """, "candidates"))!;

    public static readonly JsonNode Variations = JsonNode.Parse(Obj($$"""
        "variations": {"type":"array","items":{{Obj($$"""
            "id": {{D("string", "V1, V2…")}},
            "name": {{D("string", "Short internal creative name.")}},
            "angle": {{Str}},
            "hook": {{D("string", "The opening line/visual in one sentence.")}},
            "scenes": {"type":"array","items":{{Obj($$"""
                "number": {"type":"integer"},
                "role": {{RoleEnum}},
                "timing": {{D("string", "e.g. 0:00–0:03")}},
                "voiceOver": {{Str}},
                "onScreenText": {{D("string", "Max ~7 words.")}},
                "visualDirection": {{D("string", "Shot, framing, action, transition, SFX — concrete enough for a motion designer.")}}
                """, "number", "role", "timing", "voiceOver", "onScreenText", "visualDirection")}}},
            "inspiredBy": {"type":"array","items":{{Obj($$"""
                "matchId": {{Str}}, "techniqueName": {{Str}}, "competitorTimestamp": {{Str}}, "howApplied": {{Str}}
                """, "matchId", "techniqueName", "competitorTimestamp", "howApplied")}}},
            "estimatedDurationSec": {"type":"integer"}
            """, "id", "name", "angle", "hook", "scenes", "inspiredBy", "estimatedDurationSec")}}}
        """, "variations"))!;
}
