#!/bin/bash
# Entrypoint du container d'entraînement — délègue tout à train.py.
set -euo pipefail
exec python /train.py
