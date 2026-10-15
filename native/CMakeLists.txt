cmake_minimum_required(VERSION 3.20)
project(ffa_diskann LANGUAGES CXX)
if(NOT MSVC OR NOT CMAKE_SIZEOF_VOID_P EQUAL 8)
  message(FATAL_ERROR "The current DiskANN core is built with MSVC for Windows x64.")
endif()
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(SOURCE_FILES
  abstract_data_store.cpp partition.cpp pq.cpp pq_flash_index.cpp logger.cpp utils.cpp
  windows_aligned_file_reader.cpp distance.cpp pq_l2_distance.cpp memory_mapper.cpp index.cpp
  in_mem_data_store.cpp pq_data_store.cpp in_mem_graph_store.cpp math_utils.cpp disk_utils.cpp
  filter_utils.cpp ann_exception.cpp natural_number_set.cpp natural_number_map.cpp scratch.cpp
  index_factory.cpp abstract_index.cpp)
list(TRANSFORM SOURCE_FILES PREPEND "${DISKANN_SOURCE}/src/")
add_library(ffa_diskann SHARED bridge.cpp ${SOURCE_FILES})
target_include_directories(ffa_diskann PRIVATE
  "${DISKANN_SOURCE}/include"
  "${NATIVE_PACKAGES}/boost/lib/native/include"
  "${NATIVE_PACKAGES}/intelopenmp.devel.win/lib/native/include"
  "${NATIVE_PACKAGES}/intelmkl.static.win-x64/lib/native/include")
target_compile_definitions(ffa_diskann PRIVATE
  _USRDLL _WINDLL _WINDOWS NOMINMAX UNICODE MKL_ILP64 USE_AVX2 USE_ACCELERATED_PQ ENABLE_CUSTOM_LOGGER)
target_compile_options(ffa_diskann PRIVATE /O2 /EHsc /MD /bigobj /openmp /arch:AVX2 /fp:fast /permissive- /W3 /WX-)
target_link_options(ffa_diskann PRIVATE /NODEFAULTLIB:VCOMP.LIB /NODEFAULTLIB:VCOMPD.LIB)
set(MKL "${NATIVE_PACKAGES}/intelmkl.static.win-x64/lib/native/win-x64")
target_link_libraries(ffa_diskann PRIVATE
  "${MKL}/mkl_intel_ilp64.lib" "${MKL}/mkl_core.lib" "${MKL}/mkl_intel_thread.lib"
  "${NATIVE_PACKAGES}/intelopenmp.devel.win/lib/native/win-x64/libiomp5md.lib"
  synchronization.lib)
set_target_properties(ffa_diskann PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${NATIVE_OUTPUT}"
  ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib")
add_custom_command(TARGET ffa_diskann POST_BUILD COMMAND ${CMAKE_COMMAND} -E copy_if_different
  "${NATIVE_PACKAGES}/intelopenmp.redist.win/runtimes/win-x64/native/libiomp5md.dll" "${NATIVE_OUTPUT}")
add_executable(ffa_diskann_smoke smoke.cpp)
target_link_libraries(ffa_diskann_smoke PRIVATE ffa_diskann)
set_target_properties(ffa_diskann_smoke PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${NATIVE_OUTPUT}")
