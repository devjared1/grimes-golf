# Writes the licences built into this Godot engine to the file named after "--": see tools/make-notices.ps1.
extends SceneTree

func _init():
	var out := FileAccess.open(OS.get_cmdline_user_args()[0], FileAccess.WRITE)
	out.store_string("GODOT ENGINE\n\n" + Engine.get_license_text() + "\n\nCOMPONENTS INSIDE GODOT ENGINE\n\n")
	for part in Engine.get_copyright_info():
		out.store_string(str(part["name"]) + "\n")
		for entry in part["parts"]:
			out.store_string("  Files: " + ", ".join(entry["files"]) + "\n")
			for line in entry["copyright"]:
				out.store_string("  (c) " + line + "\n")
			out.store_string("  Licence: " + entry["license"] + "\n")
		out.store_string("\n")
	out.store_string("LICENCE TEXTS\n\n")
	var licences := Engine.get_license_info()
	for name in licences:
		out.store_string("----- " + name + " -----\n\n" + licences[name] + "\n\n")
	out.close()
	quit()
