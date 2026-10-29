import asyncio

from loom_orchestrator.stt_parakeet import ParakeetSpeakerView, text_delta, valid_feature_frames


def test_valid_feature_frames_needs_half_window_after_frame_centre():
    assert valid_feature_frames(0, hop=160, n_fft=512) == 0
    assert valid_feature_frames(255, hop=160, n_fft=512) == 0
    assert valid_feature_frames(256, hop=160, n_fft=512) == 1
    assert valid_feature_frames(16_000, hop=160, n_fft=512) == 99


def test_text_delta_is_appended_text_or_none_on_revision():
    assert text_delta("", "Call me") == "Call me"
    assert text_delta("Call me", "Call me Ishmael.") == " Ishmael."
    assert text_delta("Call me", "Call me") == ""
    assert text_delta("Call me", "Cell me Ishmael") is None


def _drain(view: ParakeetSpeakerView) -> list[list[dict]]:
    async def run():
        out = []
        async for response in await view.create_tasks():
            out.append(response.to_dict()["lines"])
        return out

    return asyncio.run(run())


def test_speaker_view_seals_sentences_pauses_and_closes():
    view = ParakeetSpeakerView(speaker="1", pause_seal_s=1.0)
    view.apply("Call me Ishmael. Some years", 2.0)
    view.seal_if_paused(2.5)  # pause trop courte : rien
    view.seal_if_paused(3.5)  # pause : la ligne active est scellée
    view.apply(" ago,", 4.0)
    view.close()
    snapshots = _drain(view)
    texts = [[line["text"] for line in snapshot] for snapshot in snapshots]
    assert texts[0] == ["Call me Ishmael.", "Some years"]
    # Tout est scellé : une ligne active vide suit, pour que la dernière soit traduite.
    assert texts[1] == ["Call me Ishmael.", "Some years", ""]
    assert texts[-1] == ["Call me Ishmael.", "Some years", "ago,", ""]
    assert len(snapshots) == 4
