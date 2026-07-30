namespace BeeX.OCR;

internal sealed record OcrRecognitionReport(
    string Text,
    string CandidateName,
    int Score,
    IReadOnlyList<OcrCandidateInfo> Candidates);

internal sealed record OcrCandidateInfo(string CandidateName, string Text, int Score);
