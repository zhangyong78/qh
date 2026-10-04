import os
import runpy

os.environ["QH_TQSDK_PATH"] = os.environ["QH_G4_FAKE_SDK"]
runpy.run_path(os.environ["QH_G4_REAL_BRIDGE"], run_name="__main__")
