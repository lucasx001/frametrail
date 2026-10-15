#pragma once
#include "project.hpp"

namespace frametrail::core {
// Stateless commands: the caller owns history; every result is fully validated.
Project edit_project(const Project& project, std::string_view command_json);
std::string timeline_json(const Project& project);
}
