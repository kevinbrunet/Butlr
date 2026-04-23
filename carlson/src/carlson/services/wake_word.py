"""Wake word service — openWakeWord wrapped as a Pipecat processor.

The processor consumes raw audio frames and emits a `WakeWordDetected` event
when the score on the custom model crosses `threshold`. After detection, we
open a "session" — the pipeline accepts VAD-driven user turns until N seconds
of silence close the session.
"""

from __future__ import annotations

from ..config import Config


def build_wake_word_service(config: Config):
    # from openwakeword.model import Model
    # model = Model(wakeword_models=[config.wakeword_model], ...)
    # return WakeWordProcessor(model=model, threshold=config.wakeword_threshold)
    raise NotImplementedError("Wake word wiring — requires custom model training first.")
